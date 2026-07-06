using DeviceSync.Application;
using DeviceSync.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceSync.Infrastructure;

public sealed class DeviceServerManager : IDeviceServer, IDiscoveryControl
{
    private const string ServiceType = "_devicesync._tcp";
    private readonly TcpDeviceServer _server;
    private readonly IServiceDiscoveryPublisher _publisher;
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private readonly IDeviceIdentityKeyProvider _keyProvider;
    private readonly IPairingSessionManager _pairingSessionManager;
    private readonly ILocalNetworkAddressProvider _addressProvider;
    private readonly ILogger<DeviceServerManager> _logger;
    private PublishedService? _lastService;

    public DeviceServerManager(
        TcpDeviceServer server,
        IServiceDiscoveryPublisher publisher,
        IWindowsDeviceIdentityProvider identityProvider,
        IDeviceIdentityKeyProvider keyProvider,
        IPairingSessionManager pairingSessionManager,
        ILocalNetworkAddressProvider addressProvider,
        ILogger<DeviceServerManager>? logger = null)
    {
        _server = server;
        _publisher = publisher;
        _identityProvider = identityProvider;
        _keyProvider = keyProvider;
        _pairingSessionManager = pairingSessionManager;
        _addressProvider = addressProvider;
        _logger = logger ?? NullLogger<DeviceServerManager>.Instance;
        _server.StateChanged += (_, args) => StateChanged?.Invoke(this, args);
        _server.SessionChanged += (_, args) => SessionChanged?.Invoke(this, args);
        _pairingSessionManager.StateChanged += (_, _) => _ = RestartDiscoveryAsync();
    }

    public int Port => _server.Port;
    public bool IsRunning => _server.IsRunning;
    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;
    public event EventHandler<DeviceSessionChangedEventArgs>? SessionChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        if (_server.IsRunning)
        {
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _publisher.StopAsync(cancellationToken).ConfigureAwait(false);
        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        return _server.DisconnectActiveSessionAsync(cancellationToken);
    }

    public async Task RestartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (!_server.IsRunning)
        {
            return;
        }

        await _publisher.StopAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var settings = await _identityProvider.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var deviceId = await _identityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = await _keyProvider.GetPublicKeyFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var advertisedAddress = _addressProvider.GetPrimaryLocalIPv4Address();
        if (string.IsNullOrWhiteSpace(advertisedAddress))
        {
            _logger.LogWarning("Service publication skipped: no local network address was found");
            return;
        }

        var instanceName = string.IsNullOrWhiteSpace(settings.DeviceName)
            ? Environment.MachineName
            : settings.DeviceName;

        var service = new PublishedService
        {
            InstanceName = instanceName,
            ServiceType = ServiceType,
            Port = _server.Port,
            AdvertisedAddress = advertisedAddress,
            TxtRecords = new Dictionary<string, string>
            {
                ["deviceId"] = deviceId,
                ["deviceName"] = instanceName,
                ["deviceType"] = "windows",
                ["protocolMin"] = ProtocolConstants.ProtocolVersion.ToString(),
                ["protocolMax"] = ProtocolConstants.ProtocolVersion.ToString(),
                ["appVersion"] = typeof(DeviceServerManager).Assembly.GetName().Version?.ToString() ?? "1.0",
                ["pairingAvailable"] = "false",
                ["capabilities"] = string.Join(',', SupportedCapabilities.Values),
                ["identityFingerprint"] = fingerprint,
                ["authVersion"] = "1",
            },
        };
        if (_pairingSessionManager.State is PairingState.WaitingForDevice or PairingState.ProofVerified or PairingState.WaitingForUserConfirmation)
        {
            ((Dictionary<string, string>)service.TxtRecords)["pairingAvailable"] = "true";
        }

        _lastService = service;
        try
        {
            _logger.LogInformation("Service publication requested at {AdvertisedAddress}", advertisedAddress);
            await _publisher.StartAsync(service, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Service publication failed");
        }
    }
}
