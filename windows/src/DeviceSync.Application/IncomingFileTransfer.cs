namespace DeviceSync.Application;

public enum IncomingFileTransferState
{
    Offered,
    WaitingForUser,
    Accepted,
    Receiving,
    Verifying,
    Completed,
    Rejected,
    Cancelled,
    Failed,
}

public sealed class IncomingFileTransfer
{
    public required string TransferId { get; init; }
    public required string SenderDeviceId { get; init; }
    public required string FileName { get; init; }
    public required string SafeFileName { get; init; }
    public required string MimeType { get; init; }
    public required long SizeBytes { get; init; }
    public required string ExpectedSha256 { get; init; }
    public string? FolderSyncId { get; init; }
    public string? RelativePath { get; init; }
    public long ReceivedBytes { get; internal set; }
    public int NextChunkIndex { get; internal set; }
    public string? TemporaryPath { get; internal set; }
    public string? DestinationPath { get; internal set; }
    public IncomingFileTransferState State { get; internal set; } = IncomingFileTransferState.Offered;
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset LastActivityAtUtc { get; internal set; }
    public string? Error { get; internal set; }
}
