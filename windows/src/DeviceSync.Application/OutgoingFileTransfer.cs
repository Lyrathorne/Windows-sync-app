namespace DeviceSync.Application;

public enum OutgoingFileTransferState
{
    Hashing,
    WaitingForReceiver,
    Sending,
    WaitingForConfirmation,
    Completed,
    Rejected,
    Cancelled,
    Failed,
}

public sealed class OutgoingFileTransfer
{
    public required string TransferId { get; init; }
    public required string ReceiverDeviceId { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public required long SizeBytes { get; init; }
    public string? Sha256 { get; internal set; }
    public long SentBytes { get; internal set; }
    public int NextChunkIndex { get; internal set; }
    public OutgoingFileTransferState State { get; internal set; } = OutgoingFileTransferState.Hashing;
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAtUtc { get; internal set; } = DateTimeOffset.UtcNow;
    public string? Error { get; internal set; }
}
