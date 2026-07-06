namespace DeviceSync.Application;

public enum PairingState
{
    Disabled,
    Starting,
    WaitingForDevice,
    ProofVerified,
    WaitingForUserConfirmation,
    Completing,
    Completed,
    Expired,
    Rejected,
    Failed,
}

public sealed record PairingSession
{
    public required string SessionId { get; init; }
    public required byte[] PairingSecret { get; init; }
    public required byte[] WindowsNonce { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public int FailedAttemptCount { get; init; }
    public bool IsConsumed { get; init; }
}

public sealed record PairingQrPayload
{
    public required string Format { get; init; }
    public required int Version { get; init; }
    public required string SessionId { get; init; }
    public required string PairingSecret { get; init; }
    public required string ExpiresAtUtc { get; init; }
    public IReadOnlyList<string> HostAddresses { get; init; } = [];
    public required int Port { get; init; }
    public required string WindowsDeviceId { get; init; }
    public required string WindowsDeviceName { get; init; }
    public required string WindowsIdentityPublicKey { get; init; }
    public required string WindowsIdentityFingerprint { get; init; }
    public required int ProtocolMin { get; init; }
    public required int ProtocolMax { get; init; }
}

public sealed record PinnedDeviceIdentity
{
    public required string DeviceId { get; init; }
    public required string IdentityPublicKeyFingerprint { get; init; }
    public string? FutureTlsCertificateFingerprint { get; init; }
}
