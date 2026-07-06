using DeviceSync.Protocol;

namespace DeviceSync.Application;

public interface IWindowsDeviceIdentityProvider
{
    Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IDeviceServer
{
    int Port { get; }
    bool IsRunning { get; }
    event EventHandler<ServerStateChangedEventArgs>? StateChanged;
    event EventHandler<DeviceSessionChangedEventArgs>? SessionChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task DisconnectActiveSessionAsync(CancellationToken cancellationToken = default);
}

public interface IServiceDiscoveryPublisher
{
    PublisherState State { get; }
    PublishedService? CurrentService { get; }
    string? LastError { get; }
    DateTimeOffset? LastPublishedAtUtc { get; }
    event EventHandler<PublisherStateChangedEventArgs>? StateChanged;
    Task StartAsync(PublishedService service, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IDiscoveryControl
{
    Task RestartDiscoveryAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceMessageWriter
{
    Task EnqueueAsync(ProtocolMessage message, CancellationToken cancellationToken = default);
}
