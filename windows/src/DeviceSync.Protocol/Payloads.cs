namespace DeviceSync.Protocol;

public sealed record ConnectionHelloPayload
{
    public required string DeviceName { get; init; }
    public string DeviceType { get; init; } = "android";
    public required string AppVersion { get; init; }
    public required int ProtocolVersion { get; init; }
    public int? ProtocolMin { get; init; }
    public int? ProtocolMax { get; init; }
    public int MaxFrameBytes { get; init; } = ProtocolConstants.MaxJsonMessageSize + ProtocolConstants.FrameHeaderSize;
    public int MaxPayloadBytes { get; init; } = ProtocolConstants.MaxJsonPayloadSize;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string? IdentityFingerprint { get; init; }
    public string? ClientNonce { get; init; }
    public int AuthVersion { get; init; }
}

public sealed record ConnectionHelloAckPayload
{
    public required string DeviceName { get; init; }
    public required string DeviceType { get; init; }
    public required int AcceptedProtocolVersion { get; init; }
    public int ProtocolMin { get; init; } = ProtocolConstants.ProtocolMinVersion;
    public int ProtocolMax { get; init; } = ProtocolConstants.ProtocolMaxVersion;
    public int MaxFrameBytes { get; init; } = ProtocolConstants.MaxJsonMessageSize + ProtocolConstants.FrameHeaderSize;
    public int MaxPayloadBytes { get; init; } = ProtocolConstants.MaxJsonPayloadSize;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record PingPayload
{
    public required long Sequence { get; init; }
    public required string SentAtUtc { get; init; }
}

public sealed record PongPayload
{
    public required long Sequence { get; init; }
    public required string ReceivedAtUtc { get; init; }
}

public sealed record MessageAckPayload
{
    public required string Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record ConnectionClosePayload
{
    public string? Reason { get; init; }
    public bool AllowReconnect { get; init; } = true;
}

public sealed record ProtocolErrorPayload
{
    public required string Code { get; init; }
    public string? Message { get; init; }
    public bool Fatal { get; init; } = true;
}

public sealed record PairingRequestPayload
{
    public required string SessionId { get; init; }
    public required string AndroidDeviceId { get; init; }
    public required string AndroidDeviceName { get; init; }
    public required string AndroidAppVersion { get; init; }
    public required string AndroidIdentityPublicKey { get; init; }
    public required string AndroidIdentityFingerprint { get; init; }
    public required string AndroidNonce { get; init; }
    public required string Proof { get; init; }
}

public sealed record PairingChallengePayload
{
    public required string SessionId { get; init; }
    public required string WindowsDeviceId { get; init; }
    public required string WindowsDeviceName { get; init; }
    public required string WindowsIdentityPublicKey { get; init; }
    public required string WindowsIdentityFingerprint { get; init; }
    public required string WindowsNonce { get; init; }
    public required string AndroidNonce { get; init; }
    public required string Proof { get; init; }
}

public sealed record PairingConfirmPayload
{
    public required string SessionId { get; init; }
    public bool Confirmed { get; init; } = true;
    public required string AndroidSignature { get; init; }
}

public sealed record PairingAcceptedPayload
{
    public required string SessionId { get; init; }
    public required string WindowsSignature { get; init; }
    public required string PairedAtUtc { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

public sealed record PairingCompleteAckPayload
{
    public required string SessionId { get; init; }
    public required string Status { get; init; }
}

public sealed record AuthChallengePayload
{
    public required string ServerNonce { get; init; }
    public required string WindowsIdentityFingerprint { get; init; }
    public required string ServerSignature { get; init; }
    public required string HelloMessageId { get; init; }
    public int AcceptedProtocolVersion { get; init; } = ProtocolConstants.ProtocolVersion;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record AuthResponsePayload
{
    public required string HelloMessageId { get; init; }
    public required string ClientSignature { get; init; }
}

public sealed record AuthAcceptedPayload
{
    public required string Status { get; init; }
    public int AcceptedProtocolVersion { get; init; } = ProtocolConstants.ProtocolVersion;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record FileOfferPayload
{
    public required string TransferId { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string MimeType { get; init; }
    public required string Sha256 { get; init; }
    public required int ChunkSize { get; init; }
    public string? FolderSyncId { get; init; }
    public string? RelativePath { get; init; }
    public bool ConflictCopy { get; init; }
}

public sealed record FileAcceptPayload
{
    public required string TransferId { get; init; }
}

public sealed record FileRejectPayload
{
    public required string TransferId { get; init; }
    public required string Code { get; init; }
    public string? Message { get; init; }
}

public sealed record FileChunkPayload
{
    public required string TransferId { get; init; }
    public required int Index { get; init; }
    public required long Offset { get; init; }
    public required string Data { get; init; }
    public string? ChunkSha256 { get; init; }
}

public sealed record FileChunkReceivedPayload
{
    public required string TransferId { get; init; }
    public required int NextChunkIndex { get; init; }
    public required long Offset { get; init; }
}

public sealed record FileResumeRequestPayload
{
    public required string TransferId { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required int ChunkSize { get; init; }
    public string? FolderSyncId { get; init; }
    public string? RelativePath { get; init; }
    public bool ConflictCopy { get; init; }
}

public sealed record FileResumeAcceptedPayload
{
    public required string TransferId { get; init; }
    public required int NextChunkIndex { get; init; }
    public required long Offset { get; init; }
}

public sealed record ClipboardUpdatePayload
{
    public required string RevisionId { get; init; }
    public required string OriginDeviceId { get; init; }
    public required string ContentType { get; init; }
    public required string Text { get; init; }
    public required string CreatedAtUtc { get; init; }
    public bool IsManual { get; init; }
}

public sealed record TextSharePayload
{
    public required string ItemId { get; init; }
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public required string CreatedAtUtc { get; init; }
}

public sealed record NotificationPostedPayload
{
    public required string NotificationId { get; init; }
    public required string PackageName { get; init; }
    public required string AppName { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required string PostedAtUtc { get; init; }
    public string Category { get; init; } = "";
    public string? GroupKey { get; init; }
    public bool IsSilent { get; init; }
    public bool IsSensitive { get; init; }
    public string? IconToken { get; init; }
    public long Revision { get; init; } = 1;
    public IReadOnlyList<NotificationActionPayload> Actions { get; init; } = [];
}

public sealed record NotificationUpdatedPayload
{
    public required string NotificationId { get; init; }
    public required string PackageName { get; init; }
    public required string AppName { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required string PostedAtUtc { get; init; }
    public required string UpdatedAtUtc { get; init; }
    public string Category { get; init; } = "";
    public string? GroupKey { get; init; }
    public bool IsSilent { get; init; }
    public bool IsSensitive { get; init; }
    public string? IconToken { get; init; }
    public long Revision { get; init; } = 1;
    public IReadOnlyList<NotificationActionPayload> Actions { get; init; } = [];
}

public sealed record NotificationRemovedPayload
{
    public required string NotificationId { get; init; }
    public required string PackageName { get; init; }
    public string RemovedAtUtc { get; init; } = "";
    public string Reason { get; init; } = "removed";
    public long Revision { get; init; }
}

public sealed record NotificationActionPayload
{
    public required string ActionId { get; init; }
    public required string Title { get; init; }
    public string Semantic { get; init; } = "custom";
    public bool RequiresConfirmation { get; init; } = true;
    public bool IsDestructive { get; init; }
}

public sealed record NotificationActionInvokePayload
{
    public required string InvocationId { get; init; }
    public required string NotificationId { get; init; }
    public required string PackageName { get; init; }
    public required string ActionId { get; init; }
    public bool ConfirmedByUser { get; init; }
}

public sealed record NotificationActionResultPayload
{
    public required string InvocationId { get; init; }
    public required string NotificationId { get; init; }
    public required string ActionId { get; init; }
    public required string Status { get; init; }
    public string? Message { get; init; }
}

public sealed record FolderManifestEntryPayload
{
    public required string RelativePath { get; init; }
    public required long SizeBytes { get; init; }
    public required string LastModifiedUtc { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record FolderManifestPayload
{
    public required string SyncId { get; init; }
    public required string RootId { get; init; }
    public required string GeneratedAtUtc { get; init; }
    public required IReadOnlyList<FolderManifestEntryPayload> Entries { get; init; }
}

public sealed record FolderPlanOperationPayload
{
    public required string RelativePath { get; init; }
    public required string Action { get; init; }
    public string? Reason { get; init; }
}

public sealed record FolderPlanPayload
{
    public required string SyncId { get; init; }
    public required IReadOnlyList<FolderPlanOperationPayload> Operations { get; init; }
}

public sealed record FolderConflictResolutionPayload
{
    public required string RelativePath { get; init; }
    public required string Resolution { get; init; }
}

public sealed record FolderPlanApprovedPayload
{
    public required string SyncId { get; init; }
    public required IReadOnlyList<FolderConflictResolutionPayload> ConflictResolutions { get; init; }
}

public sealed record FileCompletePayload
{
    public required string TransferId { get; init; }
    public required int TotalChunks { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed record FileReceivedPayload
{
    public required string TransferId { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string SavedFileName { get; init; }
}

public sealed record FileCancelPayload
{
    public required string TransferId { get; init; }
    public required string Reason { get; init; }
}

public sealed record FileErrorPayload
{
    public required string TransferId { get; init; }
    public required string Code { get; init; }
    public string? Message { get; init; }
}
