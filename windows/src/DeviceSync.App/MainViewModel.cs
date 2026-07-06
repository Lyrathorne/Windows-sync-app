using System.Windows;
using System.Windows.Input;
using DeviceSync.Application;

namespace DeviceSync.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDeviceServer _server;
    private readonly IServiceDiscoveryPublisher _publisher;
    private readonly IDiscoveryControl _discoveryControl;
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private string _status = "Starting";
    private string _serverSummary = "Stopped";
    private string _windowsDeviceId = "";
    private int _port = 54321;
    private string _connectedDeviceName = "No device connected";
    private string _connectedDeviceId = "-";
    private string _connectedCapabilities = "-";
    private string _connectedAtUtc = "-";
    private string _discoverySummary = "Stopped";
    private string _discoveryInstanceName = "-";
    private string _discoveryServiceType = "_devicesync._tcp";
    private string _discoveryPublishedPort = "-";
    private string _discoveryLastError = "-";
    private string _discoveryLastPublishedAtUtc = "-";

    public MainViewModel(
        IDeviceServer server,
        IServiceDiscoveryPublisher publisher,
        IDiscoveryControl discoveryControl,
        IWindowsDeviceIdentityProvider identityProvider)
    {
        _server = server;
        _publisher = publisher;
        _discoveryControl = discoveryControl;
        _identityProvider = identityProvider;
        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        RestartDiscoveryCommand = new AsyncRelayCommand(RestartDiscoveryAsync);

        _server.StateChanged += OnServerStateChanged;
        _server.SessionChanged += OnSessionChanged;
        _publisher.StateChanged += OnPublisherStateChanged;
        _ = LoadIdentityAsync();
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RestartDiscoveryCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ServerSummary { get => _serverSummary; private set => SetProperty(ref _serverSummary, value); }
    public string WindowsDeviceId { get => _windowsDeviceId; private set => SetProperty(ref _windowsDeviceId, value); }
    public int Port { get => _port; private set => SetProperty(ref _port, value); }
    public string ConnectedDeviceName { get => _connectedDeviceName; private set => SetProperty(ref _connectedDeviceName, value); }
    public string ConnectedDeviceId { get => _connectedDeviceId; private set => SetProperty(ref _connectedDeviceId, value); }
    public string ConnectedCapabilities { get => _connectedCapabilities; private set => SetProperty(ref _connectedCapabilities, value); }
    public string ConnectedAtUtc { get => _connectedAtUtc; private set => SetProperty(ref _connectedAtUtc, value); }
    public string DiscoverySummary { get => _discoverySummary; private set => SetProperty(ref _discoverySummary, value); }
    public string DiscoveryInstanceName { get => _discoveryInstanceName; private set => SetProperty(ref _discoveryInstanceName, value); }
    public string DiscoveryServiceType { get => _discoveryServiceType; private set => SetProperty(ref _discoveryServiceType, value); }
    public string DiscoveryPublishedPort { get => _discoveryPublishedPort; private set => SetProperty(ref _discoveryPublishedPort, value); }
    public string DiscoveryLastError { get => _discoveryLastError; private set => SetProperty(ref _discoveryLastError, value); }
    public string DiscoveryLastPublishedAtUtc { get => _discoveryLastPublishedAtUtc; private set => SetProperty(ref _discoveryLastPublishedAtUtc, value); }

    private async Task LoadIdentityAsync()
    {
        WindowsDeviceId = await _identityProvider.GetOrCreateDeviceIdAsync();
        var settings = await _identityProvider.GetSettingsAsync();
        Port = settings.Port;
    }

    private async Task StartAsync()
    {
        await _server.StartAsync();
    }

    private async Task StopAsync()
    {
        await _server.StopAsync();
    }

    private async Task DisconnectAsync()
    {
        await _server.DisconnectActiveSessionAsync();
    }

    private async Task RestartDiscoveryAsync()
    {
        await _discoveryControl.RestartDiscoveryAsync();
    }

    private void OnServerStateChanged(object? sender, ServerStateChangedEventArgs e)
    {
        Dispatch(() =>
        {
            Port = e.Port;
            ServerSummary = e.IsRunning ? $"Listening on TCP {e.Port}" : "Stopped";
            Status = e.Status;
        });
    }

    private void OnSessionChanged(object? sender, DeviceSessionChangedEventArgs e)
    {
        Dispatch(() =>
        {
            if (e.Session is null)
            {
                ConnectedDeviceName = "No device connected";
                ConnectedDeviceId = "-";
                ConnectedCapabilities = "-";
                ConnectedAtUtc = "-";
                return;
            }

            ConnectedDeviceName = e.Session.DeviceName;
            ConnectedDeviceId = e.Session.DeviceId;
            ConnectedCapabilities = string.Join(", ", e.Session.Capabilities);
            ConnectedAtUtc = e.Session.ConnectedAtUtc.ToString("O");
        });
    }

    private void OnPublisherStateChanged(object? sender, PublisherStateChangedEventArgs e)
    {
        Dispatch(() =>
        {
            DiscoverySummary = e.State switch
            {
                PublisherState.Published => "Available",
                PublisherState.Failed => "Error; manual IP still works",
                _ => e.State.ToString(),
            };
            DiscoveryInstanceName = e.Service?.InstanceName ?? "-";
            DiscoveryServiceType = e.Service?.ServiceType ?? "_devicesync._tcp";
            DiscoveryPublishedPort = e.Service?.Port.ToString() ?? "-";
            DiscoveryLastError = e.LastError ?? "-";
            DiscoveryLastPublishedAtUtc = e.LastPublishedAtUtc?.ToString("O") ?? "-";
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
