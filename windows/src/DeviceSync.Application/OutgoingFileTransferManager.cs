using System.Security.Cryptography;
using System.Text.Json;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public interface IOutgoingFileTransferTransport
{
    bool IsConnected { get; }
    string? RemoteDeviceId { get; }
    bool SupportsResumableTransfers { get; }
    event EventHandler<OutgoingFileTransferMessageEventArgs>? MessageReceived;
    event EventHandler? Disconnected;
    Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default);
}

public sealed class OutgoingFileTransferMessageEventArgs : EventArgs
{
    public OutgoingFileTransferMessageEventArgs(string type, JsonElement payload)
    {
        Type = type;
        Payload = payload;
    }
    public string Type { get; }
    public JsonElement Payload { get; }
}

public sealed class OutgoingFileTransferChangedEventArgs : EventArgs
{
    public OutgoingFileTransferChangedEventArgs(OutgoingFileTransfer transfer, long bytesPerSecond = 0)
    {
        Transfer = transfer;
        BytesPerSecond = bytesPerSecond;
    }
    public OutgoingFileTransfer Transfer { get; }
    public long BytesPerSecond { get; }
}

public sealed record OutgoingTransferResumePoint(
    string TransferId,
    string Sha256,
    long AcknowledgedOffset,
    int NextChunkIndex);

public sealed record FolderTransferMetadata(string SyncId, string RelativePath, bool ConflictCopy = false);

public sealed class OutgoingFileTransferManager
{
    public const long MaxFileSizeBytes = 100L * 1024 * 1024;
    public const int ChunkSizeBytes = 64 * 1024;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);
    private readonly IOutgoingFileTransferTransport _transport;
    private readonly SemaphoreSlim _singleTransfer = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource<OutgoingFileTransferMessageEventArgs>? _response;
    private CancellationTokenSource? _activeCts;

    public OutgoingFileTransferManager(IOutgoingFileTransferTransport transport)
    {
        _transport = transport;
        _transport.MessageReceived += OnMessageReceived;
        _transport.Disconnected += OnDisconnected;
    }

    public OutgoingFileTransfer? ActiveTransfer { get; private set; }
    public event EventHandler<OutgoingFileTransferChangedEventArgs>? Changed;

    public async Task<OutgoingFileTransfer> SendAsync(
        string filePath,
        string? mimeType = null,
        CancellationToken cancellationToken = default,
        OutgoingTransferResumePoint? resumePoint = null,
        string? transferId = null,
        FolderTransferMetadata? folder = null)
    {
        await _singleTransfer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_transport.IsConnected || string.IsNullOrWhiteSpace(_transport.RemoteDeviceId))
                throw new InvalidOperationException("An authenticated device connection is required.");
            var info = new FileInfo(filePath);
            if (!info.Exists) throw new FileNotFoundException("The selected file does not exist.", filePath);
            if (info.Length > MaxFileSizeBytes) throw new InvalidOperationException("The file exceeds the 100 MiB V1 limit.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate) _activeCts = linked;
            var transfer = new OutgoingFileTransfer
            {
                TransferId = resumePoint?.TransferId ?? transferId ?? Guid.NewGuid().ToString(),
                ReceiverDeviceId = _transport.RemoteDeviceId!,
                FilePath = filePath,
                FileName = info.Name,
                MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType,
                SizeBytes = info.Length,
            };
            ActiveTransfer = transfer;
            var useV2 = _transport.SupportsResumableTransfers;
            if (resumePoint is not null && !useV2)
                throw new InvalidOperationException("The connected device does not support resumable transfers.");
            Publish(transfer);

            try
            {
                transfer.Sha256 = resumePoint?.Sha256 ?? await HashFileAsync(filePath, linked.Token).ConfigureAwait(false);
                transfer.State = OutgoingFileTransferState.WaitingForReceiver;
                Publish(transfer);
                var decisionTask = WaitForAsync(transfer.TransferId, linked.Token);
                if (resumePoint is null)
                {
                    await _transport.SendAsync(ProtocolMessageTypes.FileOffer, ProtocolSerializer.PayloadToJson(new FileOfferPayload
                    {
                        TransferId = transfer.TransferId,
                        FileName = transfer.FileName,
                        SizeBytes = transfer.SizeBytes,
                        MimeType = transfer.MimeType,
                        Sha256 = transfer.Sha256,
                        ChunkSize = ChunkSizeBytes,
                        FolderSyncId = folder?.SyncId,
                        RelativePath = folder?.RelativePath,
                        ConflictCopy = folder?.ConflictCopy ?? false,
                    }), linked.Token).ConfigureAwait(false);
                }
                else
                {
                    await _transport.SendAsync(ProtocolMessageTypes.FileResumeRequest, ProtocolSerializer.PayloadToJson(new FileResumeRequestPayload
                    {
                        TransferId = transfer.TransferId,
                        FileName = transfer.FileName,
                        SizeBytes = transfer.SizeBytes,
                        Sha256 = transfer.Sha256,
                        ChunkSize = ChunkSizeBytes,
                        FolderSyncId = folder?.SyncId,
                        RelativePath = folder?.RelativePath,
                        ConflictCopy = folder?.ConflictCopy ?? false,
                    }), linked.Token).ConfigureAwait(false);
                }

                var decision = await decisionTask.ConfigureAwait(false);
                if (decision.Type == ProtocolMessageTypes.FileReject)
                {
                    transfer.State = OutgoingFileTransferState.Rejected;
                    transfer.Error = ProtocolSerializer.DecodePayload<FileRejectPayload>(decision.Payload).Message;
                    Publish(transfer);
                    return transfer;
                }
                if (resumePoint is null)
                {
                    EnsureType(decision, ProtocolMessageTypes.FileAccept);
                }
                else
                {
                    EnsureType(decision, ProtocolMessageTypes.FileResumeAccepted);
                    var accepted = ProtocolSerializer.DecodePayload<FileResumeAcceptedPayload>(decision.Payload);
                    if (accepted.Offset < 0 || accepted.Offset > resumePoint.AcknowledgedOffset || accepted.NextChunkIndex < 0)
                        throw new InvalidDataException("Receiver returned an invalid resume point.");
                    transfer.SentBytes = accepted.Offset;
                    transfer.NextChunkIndex = accepted.NextChunkIndex;
                }

                transfer.State = OutgoingFileTransferState.Sending;
                var buffer = new byte[ChunkSizeBytes];
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSizeBytes, true);
                if (transfer.SentBytes > 0) stream.Position = transfer.SentBytes;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    var acknowledgementTask = useV2 ? WaitForAsync(transfer.TransferId, linked.Token) : null;
                    await _transport.SendAsync(ProtocolMessageTypes.FileChunk, ProtocolSerializer.PayloadToJson(new FileChunkPayload
                    {
                        TransferId = transfer.TransferId,
                        Index = transfer.NextChunkIndex,
                        Offset = transfer.SentBytes,
                        Data = Convert.ToBase64String(buffer, 0, read),
                        ChunkSha256 = useV2 ? Base64UrlEncode(SHA256.HashData(buffer.AsSpan(0, read))) : null,
                    }), linked.Token).ConfigureAwait(false);
                    transfer.SentBytes += read;
                    transfer.NextChunkIndex++;
                    if (acknowledgementTask is not null)
                    {
                        var acknowledgement = await acknowledgementTask.ConfigureAwait(false);
                        EnsureType(acknowledgement, ProtocolMessageTypes.FileChunkReceived);
                        var receivedChunk = ProtocolSerializer.DecodePayload<FileChunkReceivedPayload>(acknowledgement.Payload);
                        if (receivedChunk.NextChunkIndex != transfer.NextChunkIndex || receivedChunk.Offset != transfer.SentBytes)
                            throw new InvalidDataException("Receiver acknowledged an unexpected resume point.");
                    }
                    transfer.LastActivityAtUtc = DateTimeOffset.UtcNow;
                    Publish(transfer, stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : (long)(transfer.SentBytes / stopwatch.Elapsed.TotalSeconds));
                }

                var confirmationTask = WaitForAsync(transfer.TransferId, linked.Token);
                await _transport.SendAsync(ProtocolMessageTypes.FileComplete, ProtocolSerializer.PayloadToJson(new FileCompletePayload
                {
                    TransferId = transfer.TransferId,
                    TotalChunks = transfer.NextChunkIndex,
                    SizeBytes = transfer.SizeBytes,
                }), linked.Token).ConfigureAwait(false);
                transfer.State = OutgoingFileTransferState.WaitingForConfirmation;
                Publish(transfer);
                var confirmation = await confirmationTask.ConfigureAwait(false);
                EnsureType(confirmation, ProtocolMessageTypes.FileReceived);
                var received = ProtocolSerializer.DecodePayload<FileReceivedPayload>(confirmation.Payload);
                if (received.SizeBytes != transfer.SizeBytes || received.Sha256 != transfer.Sha256)
                    throw new InvalidDataException("Receiver confirmation does not match the sent file.");
                transfer.State = OutgoingFileTransferState.Completed;
                Publish(transfer);
                return transfer;
            }
            catch (OperationCanceledException)
            {
                transfer.State = OutgoingFileTransferState.Cancelled;
                Publish(transfer);
                throw;
            }
            catch (Exception error)
            {
                transfer.State = OutgoingFileTransferState.Failed;
                transfer.Error = error.Message;
                Publish(transfer);
                throw;
            }
            finally
            {
                lock (_gate) _activeCts = null;
            }
        }
        finally
        {
            _singleTransfer.Release();
        }
    }

    public async Task CancelAsync(string reason = "user_cancelled", CancellationToken cancellationToken = default)
    {
        var transfer = ActiveTransfer;
        if (transfer is null || transfer.State is OutgoingFileTransferState.Completed or OutgoingFileTransferState.Cancelled or OutgoingFileTransferState.Failed)
            return;
        await _transport.SendAsync(ProtocolMessageTypes.FileCancel, ProtocolSerializer.PayloadToJson(new FileCancelPayload
        {
            TransferId = transfer.TransferId,
            Reason = reason,
        }), cancellationToken).ConfigureAwait(false);
        lock (_gate) _activeCts?.Cancel();
    }

    private async Task<OutgoingFileTransferMessageEventArgs> WaitForAsync(string transferId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<OutgoingFileTransferMessageEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _response = completion;
        try { return await completion.Task.WaitAsync(ResponseTimeout, cancellationToken).ConfigureAwait(false); }
        finally { lock (_gate) if (ReferenceEquals(_response, completion)) _response = null; }
    }

    private void OnMessageReceived(object? sender, OutgoingFileTransferMessageEventArgs args)
    {
        var transfer = ActiveTransfer;
        if (transfer is null) return;
        var transferId = args.Type switch
        {
            ProtocolMessageTypes.FileAccept => ProtocolSerializer.DecodePayload<FileAcceptPayload>(args.Payload).TransferId,
            ProtocolMessageTypes.FileReject => ProtocolSerializer.DecodePayload<FileRejectPayload>(args.Payload).TransferId,
            ProtocolMessageTypes.FileReceived => ProtocolSerializer.DecodePayload<FileReceivedPayload>(args.Payload).TransferId,
            ProtocolMessageTypes.FileCancel => ProtocolSerializer.DecodePayload<FileCancelPayload>(args.Payload).TransferId,
            ProtocolMessageTypes.FileError => ProtocolSerializer.DecodePayload<FileErrorPayload>(args.Payload).TransferId,
            ProtocolMessageTypes.FileChunkReceived => ProtocolSerializer.DecodePayload<FileChunkReceivedPayload>(args.Payload).TransferId,
            ProtocolMessageTypes.FileResumeAccepted => ProtocolSerializer.DecodePayload<FileResumeAcceptedPayload>(args.Payload).TransferId,
            _ => null,
        };
        if (transferId != transfer.TransferId) return;
        if (args.Type is ProtocolMessageTypes.FileCancel or ProtocolMessageTypes.FileError)
        {
            lock (_gate) _activeCts?.Cancel();
        }
        lock (_gate) _response?.TrySetResult(args);
    }

    private void OnDisconnected(object? sender, EventArgs args)
    {
        lock (_gate) _activeCts?.Cancel();
    }

    private static void EnsureType(OutgoingFileTransferMessageEventArgs message, string expected)
    {
        if (message.Type != expected) throw new InvalidDataException($"Expected {expected}, received {message.Type}.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSizeBytes, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Publish(OutgoingFileTransfer transfer, long speed = 0) => Changed?.Invoke(this, new(transfer, speed));
}
