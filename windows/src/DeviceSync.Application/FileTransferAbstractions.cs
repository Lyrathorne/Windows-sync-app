using System.Text.Json;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public interface IIncomingFileTransferDecisionService
{
    Task<IncomingFileTransferDecision> DecideAsync(
        IncomingFileTransfer transfer,
        CancellationToken cancellationToken = default);
}

public interface IIncomingFileStorage
{
    string DefaultReceiveDirectory { get; }
    long GetAvailableBytes(string directoryPath);
    FileTransferReservation Reserve(string directoryPath, string safeFileName, string transferId);
    FileTransferReservation Reserve(string directoryPath, string safeFileName, string transferId, bool replaceExisting)
        => Reserve(directoryPath, safeFileName, transferId);
    Stream OpenWrite(string temporaryPath);
    Stream OpenResume(string temporaryPath, long offset) => OpenWrite(temporaryPath);
    Stream OpenReadPartial(string temporaryPath) => throw new NotSupportedException();
    Task<string> CommitAsync(FileTransferReservation reservation, CancellationToken cancellationToken = default);
    Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken = default);
}

public interface IIncomingTransferCheckpointStore
{
    Task SaveAsync(IncomingTransferCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<IncomingTransferCheckpoint?> LoadAsync(string transferId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string transferId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IncomingTransferCheckpoint>> GetExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);
}

public sealed record IncomingTransferCheckpoint(
    string TransferId,
    string SenderDeviceId,
    string FileName,
    string SafeFileName,
    string MimeType,
    long SizeBytes,
    string ExpectedSha256,
    int ChunkSize,
    int NextChunkIndex,
    long ReceivedBytes,
    string TemporaryPath,
    string DestinationPath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityAtUtc,
    IReadOnlyList<string> ChunkSha256);

public sealed record IncomingFileTransferDecision(
    bool Accepted,
    string? DestinationDirectory = null,
    string RejectionCode = "user_rejected",
    string? DestinationFileName = null,
    bool ReplaceExisting = false);

public interface IFolderFileTransferAuthorizer
{
    IncomingFileTransferDecision? Authorize(FileOfferPayload offer);
}

public sealed record FileTransferReservation(string TemporaryPath, string DestinationPath, bool ReplaceExisting = false);

public sealed record FileTransferResponse(string Type, JsonElement Payload);

public sealed class IncomingFileTransferDecisionCoordinator : IIncomingFileTransferDecisionService
{
    private readonly object _gate = new();
    private PendingDecision? _pending;

    public event EventHandler<IncomingFileTransferDecisionRequestedEventArgs>? DecisionRequested;

    public async Task<IncomingFileTransferDecision> DecideAsync(
        IncomingFileTransfer transfer,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<IncomingFileTransferDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("A file-transfer decision is already pending.");
            }

            _pending = new PendingDecision(transfer.TransferId, completion);
        }

        DecisionRequested?.Invoke(this, new IncomingFileTransferDecisionRequestedEventArgs(transfer));
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_pending?.TransferId == transfer.TransferId)
                {
                    _pending = null;
                }
            }
        }
    }

    public bool Accept(string transferId, string? destinationDirectory = null)
        => Complete(transferId, new IncomingFileTransferDecision(true, destinationDirectory));

    public bool Reject(string transferId, string code = "user_rejected")
        => Complete(transferId, new IncomingFileTransferDecision(false, RejectionCode: code));

    private bool Complete(string transferId, IncomingFileTransferDecision decision)
    {
        TaskCompletionSource<IncomingFileTransferDecision>? completion;
        lock (_gate)
        {
            if (_pending is null || _pending.TransferId != transferId)
            {
                return false;
            }

            completion = _pending.Completion;
        }

        return completion.TrySetResult(decision);
    }

    private sealed record PendingDecision(
        string TransferId,
        TaskCompletionSource<IncomingFileTransferDecision> Completion);
}
