using DeviceSync.Application;
using DeviceSync.Protocol;

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
