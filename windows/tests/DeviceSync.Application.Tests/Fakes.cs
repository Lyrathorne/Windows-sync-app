using DeviceSync.Application;
using DeviceSync.Protocol;
using System.Security.Cryptography;

namespace DeviceSync.Application.Tests;

internal sealed class FakeIdentityProvider : IWindowsDeviceIdentityProvider
{
    private readonly string _deviceId;
    private AppSettings _settings;

    public FakeIdentityProvider(string deviceId, int port = 54321)
    {
        _deviceId = deviceId;
        _settings = new AppSettings { WindowsDeviceId = deviceId, Port = port };
    }

    public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_deviceId);
    }

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_settings);
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}

internal static class Messages
{
    public static ProtocolMessage Hello(string deviceName = "Pixel", string deviceType = "android") => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "hello-1",
        Type = ProtocolMessageTypes.ConnectionHello,
        SenderDeviceId = "android-1",
        TimestampUtc = "2026-07-05T18:45:00Z",
        Payload = ProtocolSerializer.PayloadToJson(new ConnectionHelloPayload
        {
            DeviceName = deviceName,
            DeviceType = deviceType,
            AppVersion = "1.0",
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
        }),
    };

    public static ProtocolMessage Ping() => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "ping-1",
        Type = ProtocolMessageTypes.ConnectionPing,
        SenderDeviceId = "android-1",
        RecipientDeviceId = "windows-fixed",
        TimestampUtc = "2026-07-05T18:45:02Z",
        Payload = ProtocolSerializer.PayloadToJson(new PingPayload
        {
            Sequence = 42,
            SentAtUtc = "2026-07-05T18:45:02Z",
        }),
    };
}

internal sealed class FakeDeviceIdentityKeyProvider : IDeviceIdentityKeyProvider, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_key.ExportSubjectPublicKeyInfo());
    }

    public async Task<string> GetPublicKeyFingerprintAsync(CancellationToken cancellationToken = default)
    {
        return SecurityEncoding.Fingerprint(await GetPublicKeyAsync(cancellationToken));
    }

    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_key.SignData(data.Span, HashAlgorithmName.SHA256));
    }

    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public void Dispose()
    {
        _key.Dispose();
    }
}

internal sealed class FakeTrustedDeviceRepository : ITrustedDeviceRepository
{
    private readonly Dictionary<string, TrustedDevice> _devices = [];

    public Task<IReadOnlyList<TrustedDevice>> GetTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<TrustedDevice>>(_devices.Values.ToList());
    }

    public Task<TrustedDevice?> GetTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        _devices.TryGetValue(deviceId, out var device);
        return Task.FromResult(device);
    }

    public Task SaveTrustedDeviceAsync(TrustedDevice device, CancellationToken cancellationToken = default)
    {
        _devices[device.DeviceId] = device;
        return Task.CompletedTask;
    }

    public Task ActivateTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_devices.TryGetValue(deviceId, out var device))
        {
            _devices[deviceId] = device with { TrustStatus = TrustStatuses.Active };
        }
        return Task.CompletedTask;
    }

    public Task UpdateLastVerifiedAtAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        if (_devices.TryGetValue(deviceId, out var device))
        {
            _devices[deviceId] = device with { LastVerifiedAtUtc = timestamp };
        }
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        if (_devices.TryGetValue(deviceId, out var device))
        {
            _devices[deviceId] = device with { RevokedAtUtc = timestamp, TrustStatus = TrustStatuses.Revoked };
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        _devices.Remove(deviceId);
        return Task.CompletedTask;
    }
}
