using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.IO;
using DeviceSync.Application;

namespace DeviceSync.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDeviceServer _server;
    private readonly IServiceDiscoveryPublisher _publisher;
    private readonly IDiscoveryControl _discoveryControl;
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private readonly IPairingSessionManager _pairingSessionManager;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly System.Windows.Threading.DispatcherTimer _pairingTimer;
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
    private string _pairingState = "Disabled";
    private string _pairingTimeRemaining = "-";
    private string _pairingDeviceName = "-";
    private string _pairingVerificationCode = "-";
    private ImageSource? _pairingQrImage;
    private bool _isPairingVisible;

    public MainViewModel(
        IDeviceServer server,
        IServiceDiscoveryPublisher publisher,
        IDiscoveryControl discoveryControl,
        IPairingSessionManager pairingSessionManager,
        IQrCodeGenerator qrCodeGenerator,
        IWindowsDeviceIdentityProvider identityProvider)
    {
        _server = server;
        _publisher = publisher;
        _discoveryControl = discoveryControl;
        _pairingSessionManager = pairingSessionManager;
        _qrCodeGenerator = qrCodeGenerator;
        _identityProvider = identityProvider;
        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        RestartDiscoveryCommand = new AsyncRelayCommand(RestartDiscoveryAsync);
        AddPhoneCommand = new AsyncRelayCommand(StartPairingAsync);
        CancelPairingCommand = new AsyncRelayCommand(CancelPairingAsync);
        NewPairingCodeCommand = new AsyncRelayCommand(StartPairingAsync);
        ConfirmPairingCodeCommand = new AsyncRelayCommand(ConfirmPairingCodeAsync);
        RejectPairingCodeCommand = new AsyncRelayCommand(RejectPairingCodeAsync);

        _server.StateChanged += OnServerStateChanged;
        _server.SessionChanged += OnSessionChanged;
        _publisher.StateChanged += OnPublisherStateChanged;
        _pairingSessionManager.StateChanged += OnPairingStateChanged;
        _pairingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _pairingTimer.Tick += (_, _) => UpdatePairingTimer();
        _ = LoadIdentityAsync();
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RestartDiscoveryCommand { get; }
    public ICommand AddPhoneCommand { get; }
    public ICommand CancelPairingCommand { get; }
    public ICommand NewPairingCodeCommand { get; }
    public ICommand ConfirmPairingCodeCommand { get; }
    public ICommand RejectPairingCodeCommand { get; }
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
    public string PairingState { get => _pairingState; private set => SetProperty(ref _pairingState, value); }
    public string PairingTimeRemaining { get => _pairingTimeRemaining; private set => SetProperty(ref _pairingTimeRemaining, value); }
    public string PairingDeviceName { get => _pairingDeviceName; private set => SetProperty(ref _pairingDeviceName, value); }
    public string PairingVerificationCode { get => _pairingVerificationCode; private set => SetProperty(ref _pairingVerificationCode, value); }
    public ImageSource? PairingQrImage { get => _pairingQrImage; private set => SetProperty(ref _pairingQrImage, value); }
    public bool IsPairingVisible { get => _isPairingVisible; private set => SetProperty(ref _isPairingVisible, value); }

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

    private async Task StartPairingAsync()
    {
        if (!_server.IsRunning)
        {
            await _server.StartAsync();
        }

        var payload = await _pairingSessionManager.StartPairingAsync(_server.Port, GetLocalIPv4Addresses());
        var content = JsonSerializer.Serialize(payload, ProtocolSerializerOptions.CamelCase);
        PairingQrImage = PngToImageSource(_qrCodeGenerator.GeneratePng(content, 8));
        PairingDeviceName = payload.WindowsDeviceName;
        PairingVerificationCode = "-";
        IsPairingVisible = true;
        UpdatePairingState();
        UpdatePairingTimer();
        _pairingTimer.Start();
        await _discoveryControl.RestartDiscoveryAsync();
    }

    private async Task CancelPairingAsync()
    {
        await _pairingSessionManager.CancelAsync();
        PairingQrImage = null;
        IsPairingVisible = false;
        _pairingTimer.Stop();
        await _discoveryControl.RestartDiscoveryAsync();
    }

    private Task ConfirmPairingCodeAsync()
    {
        PairingState = "Local confirmation received; waiting for remote confirmation";
        return Task.CompletedTask;
    }

    private async Task RejectPairingCodeAsync()
    {
        await CancelPairingAsync();
        PairingState = "Rejected";
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

    private void OnPairingStateChanged(object? sender, EventArgs e)
    {
        Dispatch(UpdatePairingState);
    }

    private void UpdatePairingState()
    {
        PairingState = _pairingSessionManager.State.ToString();
        if (_pairingSessionManager.CurrentSession is null &&
            _pairingSessionManager.State is DeviceSync.Application.PairingState.Expired or DeviceSync.Application.PairingState.Disabled)
        {
            PairingTimeRemaining = "-";
        }
    }

    private void UpdatePairingTimer()
    {
        var session = _pairingSessionManager.CurrentSession;
        if (session is null)
        {
            PairingTimeRemaining = "-";
            return;
        }

        var remaining = session.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            PairingTimeRemaining = "expired";
            PairingState = "Expired";
            _pairingTimer.Stop();
            return;
        }

        PairingTimeRemaining = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
    }

    private static ImageSource PngToImageSource(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static IReadOnlyList<string> GetLocalIPv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address))
            .Select(address => address.ToString())
            .Distinct()
            .ToList();
    }

    private static class ProtocolSerializerOptions
    {
        public static readonly JsonSerializerOptions CamelCase = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
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
