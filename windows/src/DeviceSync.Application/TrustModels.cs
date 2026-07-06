namespace DeviceSync.Application;

public sealed record TrustedDevice
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string IdentityPublicKey { get; init; }
    public required string IdentityFingerprint { get; init; }
    public string? FutureTlsCertificateFingerprint { get; init; }
    public required DateTimeOffset PairedAtUtc { get; init; }
    public DateTimeOffset? LastVerifiedAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
    public string TrustStatus { get; init; } = TrustStatuses.Active;
}

public static class TrustStatuses
{
    public const string Pending = "Pending";
    public const string Active = "Active";
    public const string Revoked = "Revoked";
}

public interface ITrustedDeviceRepository
{
    Task<IReadOnlyList<TrustedDevice>> GetTrustedDevicesAsync(CancellationToken cancellationToken = default);
    Task<TrustedDevice?> GetTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task SaveTrustedDeviceAsync(TrustedDevice device, CancellationToken cancellationToken = default);
    Task ActivateTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task UpdateLastVerifiedAtAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default);
    Task RevokeAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default);
    Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default);
}
