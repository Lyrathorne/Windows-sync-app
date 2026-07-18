using System.Net;
using System.Net.Sockets;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.IntegrationTests;

public sealed class NotificationLoopbackTests
{
    [Fact]
    public async Task LoopbackSession_RoutesPostedUpdatedAndRemovedWithoutDuplicates()
    {
        var port = GetAvailablePort();
        var featureTransport = new FeatureMessageTransport();
        var manager = new NotificationManager(featureTransport);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Posted += _ => posted.TrySetResult();
        manager.Removed += _ => removed.TrySetResult();

        await using var server = new TcpDeviceServer(
            new IdentityProvider(port),
            new DeviceSessionRegistry(),
            featureMessageTransport: featureTransport);
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var reader = new ProtocolFrameReader(tcp.GetStream());
        var writer = new ProtocolFrameWriter(tcp.GetStream());

        await writer.WriteAsync(Message(ProtocolMessageTypes.ConnectionHello, new ConnectionHelloPayload
        {
            DeviceName = "Android notifications",
            DeviceType = "android",
            AppVersion = "test",
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
        }));
        Assert.Equal(ProtocolMessageTypes.ConnectionHelloAck, (await reader.ReadAsync()).Type);

        await writer.WriteAsync(Message(ProtocolMessageTypes.NotificationPosted, Posted(1, "one")));
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(Message(ProtocolMessageTypes.NotificationUpdated, Updated(2, "two")));
        await writer.WriteAsync(Message(ProtocolMessageTypes.NotificationUpdated, Updated(1, "stale")));

        Assert.True(SpinWait.SpinUntil(
            () => manager.History.SingleOrDefault()?.Revision == 2,
            TimeSpan.FromSeconds(5)));
        Assert.Equal("two", Assert.Single(manager.History).Text);

        await writer.WriteAsync(Message(ProtocolMessageTypes.NotificationRemoved, new NotificationRemovedPayload
        {
            NotificationId = "loopback-id",
            PackageName = "org.example.chat",
            RemovedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Revision = 3,
        }));
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(manager.History);

        await server.StopAsync();
    }

    private static NotificationPostedPayload Posted(long revision, string text) => new()
    {
        NotificationId = "loopback-id",
        PackageName = "org.example.chat",
        AppName = "Example Chat",
        Title = "Alice",
        Text = text,
        PostedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        Revision = revision,
    };

    private static NotificationUpdatedPayload Updated(long revision, string text) => new()
    {
        NotificationId = "loopback-id",
        PackageName = "org.example.chat",
        AppName = "Example Chat",
        Title = "Alice",
        Text = text,
        PostedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        Revision = revision,
    };

    private static ProtocolMessage Message<T>(string type, T payload) => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = Guid.NewGuid().ToString(),
        Type = type,
        SenderDeviceId = "android-loopback",
        RecipientDeviceId = "windows-loopback",
        TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        Payload = ProtocolSerializer.PayloadToJson(payload),
    };

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class IdentityProvider(int port) : IWindowsDeviceIdentityProvider
    {
        private AppSettings _settings = new()
        {
            WindowsDeviceId = "windows-loopback",
            DeviceName = "Windows loopback",
            Port = port,
        };

        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings.WindowsDeviceId!);
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }
}
