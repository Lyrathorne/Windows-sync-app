namespace DeviceSync.Protocol;

public sealed record ConnectionHelloPayload
{
    public required string DeviceName { get; init; }
    public string DeviceType { get; init; } = "android";
    public required string AppVersion { get; init; }
    public required int ProtocolVersion { get; init; }
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
}

public sealed record AuthResponsePayload
{
    public required string HelloMessageId { get; init; }
    public required string ClientSignature { get; init; }
}

public sealed record AuthAcceptedPayload
{
    public required string Status { get; init; }
}
