using System.Security.Cryptography;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed class IncomingFileTransferManager
{
    public const long MaximumFileSize = 104_857_600;
    public const int RequiredChunkSize = 65_536;

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IIncomingFileStorage _storage;
    private readonly IIncomingFileTransferDecisionService _decisionService;
    private readonly TimeProvider _timeProvider;
    private readonly IIncomingTransferCheckpointStore? _checkpointStore;
    private readonly IFolderFileTransferAuthorizer? _folderAuthorizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _seenTransferIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTransferResponse> _completedResponses = new(StringComparer.Ordinal);
    private TransferContext? _context;

    public IncomingFileTransferManager(
        IIncomingFileStorage storage,
        IIncomingFileTransferDecisionService decisionService,
        TimeProvider? timeProvider = null,
        IIncomingTransferCheckpointStore? checkpointStore = null,
        IFolderFileTransferAuthorizer? folderAuthorizer = null)
    {
        _storage = storage;
        _decisionService = decisionService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkpointStore = checkpointStore;
        _folderAuthorizer = folderAuthorizer;
    }

    public IncomingFileTransfer? ActiveTransfer => _context?.Transfer;

    public event EventHandler<IncomingFileTransferChangedEventArgs>? TransferChanged;
    public event EventHandler<IncomingFileTransferProgressEventArgs>? ProgressChanged;
    public event EventHandler<FileTransferResponseRequestedEventArgs>? ResponseRequested;

    public async Task<FileTransferResponse?> HandleOfferAsync(
        string senderDeviceId,
        FileOfferPayload offer,
        CancellationToken cancellationToken = default,
        bool resumable = false)
    {
        TransferContext context;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_context is { Transfer.State: not IncomingFileTransferState.Completed
                    and not IncomingFileTransferState.Rejected
                    and not IncomingFileTransferState.Cancelled
                    and not IncomingFileTransferState.Failed })
            {
                return Reject(offer.TransferId, "busy", "Another file transfer is active.");
            }

            var validationError = ValidateOffer(offer, out var safeFileName);
            if (validationError is not null)
            {
                return Reject(offer.TransferId, validationError.Value.Code, validationError.Value.Message);
            }
            if (!_seenTransferIds.Add(offer.TransferId))
            {
                return Reject(offer.TransferId, "duplicate_transfer_id", "Transfer ID was already used in this process.");
            }

            var now = _timeProvider.GetUtcNow();
            var transfer = new IncomingFileTransfer
            {
                TransferId = offer.TransferId,
                SenderDeviceId = senderDeviceId,
                FileName = offer.FileName,
                SafeFileName = safeFileName!,
                MimeType = offer.MimeType,
                SizeBytes = offer.SizeBytes,
                ExpectedSha256 = offer.Sha256,
                FolderSyncId = offer.FolderSyncId,
                RelativePath = offer.RelativePath,
                StartedAtUtc = now,
                LastActivityAtUtc = now,
                State = IncomingFileTransferState.WaitingForUser,
            };
            context = new TransferContext(transfer, offer.ChunkSize, resumable && _checkpointStore is not null);
            _context = context;
            PublishChanged(transfer);
        }
        finally
        {
            _gate.Release();
        }

        IncomingFileTransferDecision decision;
        using var offerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.Lifetime.Token,
            offerTimeout.Token);
        try
        {
            decision = _folderAuthorizer?.Authorize(offer)
                ?? await _decisionService.DecideAsync(context.Transfer, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            decision = new IncomingFileTransferDecision(false, RejectionCode: "offer_timeout");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_context, context) || context.Transfer.State != IncomingFileTransferState.WaitingForUser)
            {
                return null;
            }

            if (!decision.Accepted)
            {
                context.Transfer.State = IncomingFileTransferState.Rejected;
                context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
                PublishChanged(context.Transfer);
                return Reject(context.Transfer.TransferId, decision.RejectionCode, "The receiver declined the file.");
            }

            var directory = string.IsNullOrWhiteSpace(decision.DestinationDirectory)
                ? _storage.DefaultReceiveDirectory
                : decision.DestinationDirectory;
            if (_storage.GetAvailableBytes(directory!) < context.Transfer.SizeBytes)
            {
                context.Transfer.State = IncomingFileTransferState.Failed;
                context.Transfer.Error = "insufficient_space";
                PublishChanged(context.Transfer);
                return Reject(context.Transfer.TransferId, "insufficient_space", "Not enough free disk space.");
            }

            var destinationFileName = string.IsNullOrWhiteSpace(decision.DestinationFileName)
                ? context.Transfer.SafeFileName
                : Path.GetFileName(decision.DestinationFileName);
            var reservation = _storage.Reserve(directory!, destinationFileName!, context.Transfer.TransferId, decision.ReplaceExisting);
            context.Reservation = reservation;
            context.Stream = _storage.OpenWrite(reservation.TemporaryPath);
            context.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            context.Transfer.TemporaryPath = reservation.TemporaryPath;
            context.Transfer.DestinationPath = reservation.DestinationPath;
            context.Transfer.State = IncomingFileTransferState.Accepted;
            context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
            await SaveCheckpointLockedAsync(context, cancellationToken).ConfigureAwait(false);
            PublishChanged(context.Transfer);
            return Response(ProtocolMessageTypes.FileAccept, new FileAcceptPayload { TransferId = context.Transfer.TransferId });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await FailLockedAsync(context, "io_error", error.Message).ConfigureAwait(false);
            return Error(context.Transfer.TransferId, "io_error", "Unable to create the destination file.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FileTransferResponse?> HandleChunkAsync(
        string senderDeviceId,
        FileChunkPayload chunk,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = FindActive(senderDeviceId, chunk.TransferId);
            if (context is null || context.Stream is null || context.Hash is null)
            {
                return Error(chunk.TransferId, "invalid_state", "The transfer is not accepting chunks.");
            }

            if (context.Transfer.State is not (IncomingFileTransferState.Accepted or IncomingFileTransferState.Receiving))
            {
                return Error(chunk.TransferId, "invalid_state", "The transfer is not accepting chunks.");
            }

            if (context.Resumable && chunk.Index < context.Transfer.NextChunkIndex)
            {
                if (chunk.Index < 0 || chunk.Index >= context.ChunkHashes.Count || chunk.ChunkSha256 != context.ChunkHashes[chunk.Index])
                    return await FailAndReturnLockedAsync(context, "resume_conflict", "Duplicate chunk does not match the durable chunk.").ConfigureAwait(false);
                return ChunkAcknowledgement(context);
            }

            if (chunk.Index != context.Transfer.NextChunkIndex)
            {
                return await FailAndReturnLockedAsync(context, "invalid_chunk_index", "Unexpected chunk index.").ConfigureAwait(false);
            }

            if (chunk.Offset != context.Transfer.ReceivedBytes)
            {
                return await FailAndReturnLockedAsync(context, "invalid_chunk_offset", "Unexpected chunk offset.").ConfigureAwait(false);
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(chunk.Data);
            }
            catch (FormatException)
            {
                return await FailAndReturnLockedAsync(context, "invalid_chunk_data", "Chunk data is not valid Base64.").ConfigureAwait(false);
            }

            if (bytes.Length == 0 || bytes.Length > context.ChunkSize)
            {
                return await FailAndReturnLockedAsync(context, "invalid_chunk_data", "Chunk length is invalid.").ConfigureAwait(false);
            }

            var newTotal = context.Transfer.ReceivedBytes + bytes.Length;
            if (newTotal > context.Transfer.SizeBytes)
            {
                return await FailAndReturnLockedAsync(context, "size_exceeded", "Chunk exceeds the offered file size.").ConfigureAwait(false);
            }

            if (bytes.Length != context.ChunkSize && newTotal != context.Transfer.SizeBytes)
            {
                return await FailAndReturnLockedAsync(context, "invalid_chunk_data", "Only the final chunk may be shorter.").ConfigureAwait(false);
            }

            if (context.Resumable)
            {
                var actualChunkHash = Base64UrlEncode(SHA256.HashData(bytes));
                if (string.IsNullOrWhiteSpace(chunk.ChunkSha256) || !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(actualChunkHash),
                        System.Text.Encoding.ASCII.GetBytes(chunk.ChunkSha256)))
                    return await FailAndReturnLockedAsync(context, "chunk_checksum_mismatch", "Chunk SHA-256 does not match.").ConfigureAwait(false);
                context.ChunkHashes.Add(actualChunkHash);
            }

            await context.Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            context.Hash.AppendData(bytes);
            context.Transfer.ReceivedBytes = newTotal;
            context.Transfer.NextChunkIndex++;
            context.Transfer.State = IncomingFileTransferState.Receiving;
            context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
            await context.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await SaveCheckpointLockedAsync(context, cancellationToken).ConfigureAwait(false);
            PublishProgress(context.Transfer);
            return context.Resumable ? ChunkAcknowledgement(context) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (_context is not null)
            {
                return await FailAndReturnLockedAsync(_context, "io_error", error.Message).ConfigureAwait(false);
            }

            return Error(chunk.TransferId, "io_error", "Unable to write the file.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FileTransferResponse> HandleCompleteAsync(
        string senderDeviceId,
        FileCompletePayload complete,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_completedResponses.TryGetValue(complete.TransferId, out var completedResponse))
                return completedResponse;
            var context = FindActive(senderDeviceId, complete.TransferId);
            if (context is null || context.Stream is null || context.Hash is null || context.Reservation is null)
            {
                return Error(complete.TransferId, "invalid_state", "The transfer cannot be completed.");
            }

            if (context.Transfer.ReceivedBytes != context.Transfer.SizeBytes)
            {
                return await FailAndReturnLockedAsync(context, "complete_before_all_bytes", "Not all bytes were received.").ConfigureAwait(false);
            }

            if (complete.SizeBytes != context.Transfer.SizeBytes)
            {
                return await FailAndReturnLockedAsync(context, "size_mismatch", "Completion size does not match the offer.").ConfigureAwait(false);
            }

            if (complete.TotalChunks != context.Transfer.NextChunkIndex)
            {
                return await FailAndReturnLockedAsync(context, "total_chunks_mismatch", "Chunk count does not match.").ConfigureAwait(false);
            }

            context.Transfer.State = IncomingFileTransferState.Verifying;
            context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
            PublishChanged(context.Transfer);
            await context.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await context.Stream.DisposeAsync().ConfigureAwait(false);
            context.Stream = null;

            var actualHash = Base64UrlEncode(context.Hash.GetHashAndReset());
            context.Hash.Dispose();
            context.Hash = null;
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(context.Transfer.ExpectedSha256)))
            {
                return await FailAndReturnLockedAsync(context, "checksum_mismatch", "The received SHA-256 does not match.").ConfigureAwait(false);
            }

            var finalPath = await _storage.CommitAsync(context.Reservation, cancellationToken).ConfigureAwait(false);
            context.Transfer.DestinationPath = finalPath;
            context.Transfer.TemporaryPath = null;
            context.Transfer.State = IncomingFileTransferState.Completed;
            context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
            PublishChanged(context.Transfer);
            if (_checkpointStore is not null)
                await _checkpointStore.DeleteAsync(context.Transfer.TransferId, cancellationToken).ConfigureAwait(false);
            var response = Response(ProtocolMessageTypes.FileReceived, new FileReceivedPayload
            {
                TransferId = context.Transfer.TransferId,
                SizeBytes = context.Transfer.SizeBytes,
                Sha256 = actualHash,
                SavedFileName = Path.GetFileName(finalPath),
            });
            _completedResponses[context.Transfer.TransferId] = response;
            return response;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (_context is not null)
            {
                await FailLockedAsync(_context, "io_error", error.Message).ConfigureAwait(false);
            }

            return Error(complete.TransferId, "io_error", "Unable to finalize the file.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleCancelAsync(
        string senderDeviceId,
        FileCancelPayload cancel,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = FindActive(senderDeviceId, cancel.TransferId);
            if (context is null)
            {
                return;
            }

            context.Lifetime.Cancel();
            context.Transfer.State = IncomingFileTransferState.Cancelled;
            context.Transfer.Error = cancel.Reason;
            context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
            await CleanupLockedAsync(context).ConfigureAwait(false);
            PublishChanged(context.Transfer);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleDisconnectAsync(string senderDeviceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_context is not { } context || context.Transfer.SenderDeviceId != senderDeviceId || IsTerminal(context.Transfer.State))
            {
                return;
            }

            context.Lifetime.Cancel();
            context.Transfer.State = IncomingFileTransferState.Failed;
            context.Transfer.Error = "disconnected";
            context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
            if (context.Resumable && context.Reservation is not null)
            {
                if (context.Stream is not null)
                {
                    await context.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await context.Stream.DisposeAsync().ConfigureAwait(false);
                    context.Stream = null;
                }
                context.Hash?.Dispose();
                context.Hash = null;
                await SaveCheckpointLockedAsync(context, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CleanupLockedAsync(context).ConfigureAwait(false);
            }
            PublishChanged(context.Transfer);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FileTransferResponse> HandleResumeRequestAsync(
        string senderDeviceId,
        FileResumeRequestPayload request,
        CancellationToken cancellationToken = default)
    {
        if (_checkpointStore is null)
            return Reject(request.TransferId, "resume_not_supported", "Persistent resume is unavailable.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var checkpoint = await _checkpointStore.LoadAsync(request.TransferId, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null) return Reject(request.TransferId, "resume_not_found", "No partial transfer exists.");
            if (checkpoint.SenderDeviceId != senderDeviceId || checkpoint.FileName != request.FileName ||
                checkpoint.SizeBytes != request.SizeBytes || checkpoint.ExpectedSha256 != request.Sha256 ||
                checkpoint.ChunkSize != request.ChunkSize)
                return Reject(request.TransferId, "resume_conflict", "Resume metadata does not match the original offer.");

            var transfer = new IncomingFileTransfer
            {
                TransferId = checkpoint.TransferId,
                SenderDeviceId = checkpoint.SenderDeviceId,
                FileName = checkpoint.FileName,
                SafeFileName = checkpoint.SafeFileName,
                MimeType = checkpoint.MimeType,
                SizeBytes = checkpoint.SizeBytes,
                ExpectedSha256 = checkpoint.ExpectedSha256,
                ReceivedBytes = checkpoint.ReceivedBytes,
                NextChunkIndex = checkpoint.NextChunkIndex,
                TemporaryPath = checkpoint.TemporaryPath,
                DestinationPath = checkpoint.DestinationPath,
                State = IncomingFileTransferState.Receiving,
                StartedAtUtc = checkpoint.StartedAtUtc,
                LastActivityAtUtc = _timeProvider.GetUtcNow(),
            };
            var context = new TransferContext(transfer, checkpoint.ChunkSize, true)
            {
                Reservation = new FileTransferReservation(checkpoint.TemporaryPath, checkpoint.DestinationPath),
                Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            };
            context.ChunkHashes.AddRange(checkpoint.ChunkSha256);
            await using (var partial = _storage.OpenReadPartial(checkpoint.TemporaryPath))
            {
                var buffer = new byte[RequiredChunkSize];
                int read;
                while ((read = await partial.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    context.Hash.AppendData(buffer, 0, read);
            }
            context.Stream = _storage.OpenResume(checkpoint.TemporaryPath, checkpoint.ReceivedBytes);
            _context = context;
            _seenTransferIds.Add(request.TransferId);
            PublishChanged(transfer);
            return Response(ProtocolMessageTypes.FileResumeAccepted, new FileResumeAcceptedPayload
            {
                TransferId = transfer.TransferId,
                NextChunkIndex = transfer.NextChunkIndex,
                Offset = transfer.ReceivedBytes,
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Error(request.TransferId, "resume_io_error", error.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task CleanupStalePartialsAsync(TimeSpan? maximumAge = null, CancellationToken cancellationToken = default)
    {
        if (_checkpointStore is null) return;
        var cutoff = _timeProvider.GetUtcNow() - (maximumAge ?? TimeSpan.FromDays(7));
        foreach (var checkpoint in await _checkpointStore.GetExpiredAsync(cutoff, cancellationToken).ConfigureAwait(false))
        {
            await _storage.DeleteIfExistsAsync(checkpoint.TemporaryPath, cancellationToken).ConfigureAwait(false);
            await _checkpointStore.DeleteAsync(checkpoint.TransferId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CancelByReceiverAsync(string reason = "user_cancelled", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_context is not { } context || IsTerminal(context.Transfer.State))
            {
                return;
            }

            context.Lifetime.Cancel();
            context.Transfer.State = IncomingFileTransferState.Cancelled;
            context.Transfer.Error = reason;
            context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
            await CleanupLockedAsync(context).ConfigureAwait(false);
            PublishChanged(context.Transfer);
            ResponseRequested?.Invoke(this, new FileTransferResponseRequestedEventArgs(
                context.Transfer,
                Response(ProtocolMessageTypes.FileCancel, new FileCancelPayload
                {
                    TransferId = context.Transfer.TransferId,
                    Reason = reason,
                })));
        }
        finally
        {
            _gate.Release();
        }
    }

    private (string Code, string Message)? ValidateOffer(FileOfferPayload offer, out string? safeFileName)
    {
        safeFileName = null;
        if (!Guid.TryParseExact(offer.TransferId, "D", out _))
        {
            return ("invalid_transfer_id", "Transfer ID is not a canonical UUID.");
        }

        if (offer.SizeBytes < 0 || offer.SizeBytes > MaximumFileSize)
        {
            return ("file_too_large", "File size is outside the supported range.");
        }

        if (offer.ChunkSize != RequiredChunkSize)
        {
            return ("unsupported_chunk_size", "Chunk size must be 65536 bytes.");
        }

        if (string.IsNullOrWhiteSpace(offer.MimeType) || !TryDecodeSha256(offer.Sha256))
        {
            return ("invalid_metadata", "MIME type or SHA-256 is invalid.");
        }

        if (string.IsNullOrWhiteSpace(offer.FileName) || Path.IsPathRooted(offer.FileName))
        {
            return ("invalid_file_name", "File name is empty or absolute.");
        }

        var normalized = Path.GetFileName(offer.FileName);
        if (!string.Equals(normalized, offer.FileName, StringComparison.Ordinal)
            || offer.FileName.Contains('/') || offer.FileName.Contains('\\'))
        {
            return ("invalid_file_name", "File name contains a path.");
        }

        var invalid = Path.GetInvalidFileNameChars();
        safeFileName = new string(normalized.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        if (safeFileName.Length > 200)
        {
            var extension = Path.GetExtension(safeFileName);
            safeFileName = Path.GetFileNameWithoutExtension(safeFileName)[..Math.Min(180, Path.GetFileNameWithoutExtension(safeFileName).Length)] + extension;
        }

        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName is "." or ".." || ReservedWindowsNames.Contains(stem))
        {
            return ("invalid_file_name", "File name is not valid on Windows.");
        }

        return null;
    }

    private TransferContext? FindActive(string senderDeviceId, string transferId)
    {
        return _context is { } context
            && context.Transfer.SenderDeviceId == senderDeviceId
            && context.Transfer.TransferId == transferId
            ? context
            : null;
    }

    private async Task<FileTransferResponse> FailAndReturnLockedAsync(TransferContext context, string code, string message)
    {
        await FailLockedAsync(context, code, message).ConfigureAwait(false);
        return Error(context.Transfer.TransferId, code, message);
    }

    private async Task FailLockedAsync(TransferContext context, string code, string message)
    {
        context.Lifetime.Cancel();
        context.Transfer.State = IncomingFileTransferState.Failed;
        context.Transfer.Error = code;
        context.Transfer.LastActivityAtUtc = _timeProvider.GetUtcNow();
        await CleanupLockedAsync(context).ConfigureAwait(false);
        PublishChanged(context.Transfer);
    }

    private async Task CleanupLockedAsync(TransferContext context)
    {
        if (context.Stream is not null)
        {
            await context.Stream.DisposeAsync().ConfigureAwait(false);
            context.Stream = null;
        }

        context.Hash?.Dispose();
        context.Hash = null;
        if (context.Reservation is not null)
        {
            await _storage.DeleteIfExistsAsync(context.Reservation.TemporaryPath).ConfigureAwait(false);
            context.Transfer.TemporaryPath = null;
        }
        if (_checkpointStore is not null)
            await _checkpointStore.DeleteAsync(context.Transfer.TransferId).ConfigureAwait(false);
    }

    private void PublishChanged(IncomingFileTransfer transfer)
        => TransferChanged?.Invoke(this, new IncomingFileTransferChangedEventArgs(transfer));

    private void PublishProgress(IncomingFileTransfer transfer)
    {
        var seconds = Math.Max(0.001, (_timeProvider.GetUtcNow() - transfer.StartedAtUtc).TotalSeconds);
        ProgressChanged?.Invoke(this, new IncomingFileTransferProgressEventArgs(transfer, (long)(transfer.ReceivedBytes / seconds)));
        PublishChanged(transfer);
    }

    private static bool IsTerminal(IncomingFileTransferState state)
        => state is IncomingFileTransferState.Completed or IncomingFileTransferState.Rejected
            or IncomingFileTransferState.Cancelled or IncomingFileTransferState.Failed;

    private static bool TryDecodeSha256(string value)
    {
        try
        {
            var padding = (4 - value.Length % 4) % 4;
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', padding));
            return bytes.Length == 32 && !value.Contains('=');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static FileTransferResponse Reject(string transferId, string code, string message)
        => Response(ProtocolMessageTypes.FileReject, new FileRejectPayload { TransferId = transferId, Code = code, Message = message });

    private static FileTransferResponse Error(string transferId, string code, string message)
        => Response(ProtocolMessageTypes.FileError, new FileErrorPayload { TransferId = transferId, Code = code, Message = message });

    private static FileTransferResponse Response<T>(string type, T payload)
        => new(type, ProtocolSerializer.PayloadToJson(payload));

    private static FileTransferResponse ChunkAcknowledgement(TransferContext context)
        => Response(ProtocolMessageTypes.FileChunkReceived, new FileChunkReceivedPayload
        {
            TransferId = context.Transfer.TransferId,
            NextChunkIndex = context.Transfer.NextChunkIndex,
            Offset = context.Transfer.ReceivedBytes,
        });

    private Task SaveCheckpointLockedAsync(TransferContext context, CancellationToken cancellationToken)
    {
        if (!context.Resumable || _checkpointStore is null || context.Reservation is null) return Task.CompletedTask;
        return _checkpointStore.SaveAsync(new IncomingTransferCheckpoint(
            context.Transfer.TransferId,
            context.Transfer.SenderDeviceId,
            context.Transfer.FileName,
            context.Transfer.SafeFileName,
            context.Transfer.MimeType,
            context.Transfer.SizeBytes,
            context.Transfer.ExpectedSha256,
            context.ChunkSize,
            context.Transfer.NextChunkIndex,
            context.Transfer.ReceivedBytes,
            context.Reservation.TemporaryPath,
            context.Reservation.DestinationPath,
            context.Transfer.StartedAtUtc,
            context.Transfer.LastActivityAtUtc,
            context.ChunkHashes.ToArray()), cancellationToken);
    }

    private sealed class TransferContext
    {
        public TransferContext(IncomingFileTransfer transfer, int chunkSize, bool resumable)
        {
            Transfer = transfer;
            ChunkSize = chunkSize;
            Resumable = resumable;
        }

        public IncomingFileTransfer Transfer { get; }
        public int ChunkSize { get; }
        public bool Resumable { get; }
        public List<string> ChunkHashes { get; } = [];
        public CancellationTokenSource Lifetime { get; } = new();
        public FileTransferReservation? Reservation { get; set; }
        public Stream? Stream { get; set; }
        public IncrementalHash? Hash { get; set; }
    }
}
