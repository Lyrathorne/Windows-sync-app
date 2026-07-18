using System.Net.NetworkInformation;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceSync.Infrastructure;

public sealed class DeviceServerManager : IDeviceServer, IDiscoveryControl, IDisposable
{
    private const string ServiceType = "_devicesync._tcp";
    private readonly TcpDeviceServer _server;
    private readonly BluetoothRfcommDeviceServer _bluetoothServer;
    private readonly IServiceDiscoveryPublisher _publisher;
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private readonly IDeviceIdentityKeyProvider _keyProvider;
    private readonly IPairingSessionManager _pairingSessionManager;
    private readonly ILocalNetworkAddressProvider _addressProvider;
    private readonly ILanBeaconPublisher _beaconPublisher;
    private readonly ILogger<DeviceServerManager> _logger;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly object _networkChangeGate = new();
    private CancellationTokenSource? _networkChangeCts;
    private PublishedService? _lastService;

    public DeviceServerManager(
        TcpDeviceServer server,
        BluetoothRfcommDeviceServer bluetoothServer,
        IServiceDiscoveryPublisher publisher,
        IWindowsDeviceIdentityProvider identityProvider,
        IDeviceIdentityKeyProvider keyProvider,
        IPairingSessionManager pairingSessionManager,
        ILocalNetworkAddressProvider addressProvider,
        ILanBeaconPublisher beaconPublisher,
        ILogger<DeviceServerManager>? logger = null)
    {
        _server = server;
        _bluetoothServer = bluetoothServer;
        _publisher = publisher;
        _identityProvider = identityProvider;
        _keyProvider = keyProvider;
        _pairingSessionManager = pairingSessionManager;
        _addressProvider = addressProvider;
        _beaconPublisher = beaconPublisher;
        _logger = logger ?? NullLogger<DeviceServerManager>.Instance;
        _server.StateChanged += (_, args) => StateChanged?.Invoke(this, args);
        _server.SessionChanged += (_, args) => SessionChanged?.Invoke(this, args);
        _bluetoothServer.SessionChanged += (_, args) => SessionChanged?.Invoke(this, args);
        _pairingSessionManager.StateChanged += (_, _) =>
        {
            _logger.LogInformation("Pairing state changed to {PairingState}; refreshing discovery", _pairingSessionManager.State);
            _ = RestartDiscoveryAsync();
        };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public int Port => _server.Port;
    public bool IsRunning => _server.IsRunning;
    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;
    public event EventHandler<DeviceSessionChangedEventArgs>? SessionChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        await _bluetoothServer.StartAsync(cancellationToken).ConfigureAwait(false);
        if (_server.IsRunning)
        {
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _publisher.StopAsync(cancellationToken).ConfigureAwait(false);
        await _beaconPublisher.StopAsync(cancellationToken).ConfigureAwait(false);
        await _bluetoothServer.StopAsync(cancellationToken).ConfigureAwait(false);
        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        return _server.DisconnectActiveSessionAsync(cancellationToken);
    }

    public async Task RestartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_server.IsRunning)
            {
                _logger.LogInformation("Discovery refresh skipped because the TCP server is stopped");
                return;
            }

            await _publisher.StopAsync(cancellationToken).ConfigureAwait(false);
            await _beaconPublisher.StopAsync(cancellationToken).ConfigureAwait(false);
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var settings = await _identityProvider.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var deviceId = await _identityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = await _keyProvider.GetPublicKeyFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var advertisedAddress = _addressProvider.GetPrimaryLocalIPv4Address();
        var localAddresses = _addressProvider.GetLocalIPv4Addresses();
        var endpoints = _addressProvider.GetCandidateEndpoints(_server.Port);
        _logger.LogInformation(
            "Discovery prepared with {AddressCount} IPv4 address(es) and {EndpointCount} transport endpoint(s)",
            localAddresses.Count,
            endpoints.Count);
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
                ["endpointKinds"] = string.Join(',', endpoints.Select(endpoint => endpoint.Kind).Distinct()),
                ["addressCount"] = localAddresses.Count.ToString(),
                ["addresses"] = string.Join(',', localAddresses),
                ["endpoints"] = string.Join(';', endpoints.Select(endpoint =>
                    $"{endpoint.Kind}|{endpoint.Address}|{endpoint.Port}")),
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
            await _beaconPublisher.StartAsync(service, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Service publication failed");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ScheduleNetworkRefresh();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => ScheduleNetworkRefresh();

    private void ScheduleNetworkRefresh()
    {
        CancellationToken token;
        lock (_networkChangeGate)
        {
            _networkChangeCts?.Cancel();
            _networkChangeCts?.Dispose();
            _networkChangeCts = new CancellationTokenSource();
            token = _networkChangeCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                await RestartDiscoveryAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        lock (_networkChangeGate)
        {
            _networkChangeCts?.Cancel();
            _networkChangeCts?.Dispose();
            _networkChangeCts = null;
        }
        _discoveryGate.Dispose();
    }
}
