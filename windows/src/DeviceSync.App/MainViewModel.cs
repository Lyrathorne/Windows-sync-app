using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
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
    private readonly ITrustedDeviceRepository _trustedDeviceRepository;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly ILocalNetworkAddressProvider _addressProvider;
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
    private string _advertisedAddress = "-";
    private string _tcpEndpoint = "-";
    private string _discoveryLastError = "-";
    private string _discoveryLastPublishedAtUtc = "-";
    private string _pairingState = "Disabled";
    private string _pairingTimeRemaining = "-";
    private string _pairingDeviceName = "-";
    private string _pairingVerificationCode = "-";
    private ImageSource? _pairingQrImage;
    private byte[]? _pairingQrPng;
    private bool _isPairingVisible;

    public MainViewModel(
        IDeviceServer server,
        IServiceDiscoveryPublisher publisher,
        IDiscoveryControl discoveryControl,
        IPairingSessionManager pairingSessionManager,
        ITrustedDeviceRepository trustedDeviceRepository,
        IQrCodeGenerator qrCodeGenerator,
        ILocalNetworkAddressProvider addressProvider,
        IWindowsDeviceIdentityProvider identityProvider)
    {
        _server = server;
        _publisher = publisher;
        _discoveryControl = discoveryControl;
        _pairingSessionManager = pairingSessionManager;
        _trustedDeviceRepository = trustedDeviceRepository;
        _qrCodeGenerator = qrCodeGenerator;
        _addressProvider = addressProvider;
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
        RevokeTrustedPhoneCommand = new AsyncRelayCommand<string>(RevokeTrustedPhoneAsync);
        SavePairingQrCommand = new AsyncRelayCommand(SavePairingQrAsync);

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
        _ = LoadTrustedPhonesAsync();
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
    public ICommand RevokeTrustedPhoneCommand { get; }
    public ICommand SavePairingQrCommand { get; }
    public ObservableCollection<TrustedPhoneViewModel> TrustedPhones { get; } = [];
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
    public string AdvertisedAddress { get => _advertisedAddress; private set => SetProperty(ref _advertisedAddress, value); }
    public string TcpEndpoint { get => _tcpEndpoint; private set => SetProperty(ref _tcpEndpoint, value); }
    public string DiscoveryLastError { get => _discoveryLastError; private set => SetProperty(ref _discoveryLastError, value); }
    public string DiscoveryLastPublishedAtUtc { get => _discoveryLastPublishedAtUtc; private set => SetProperty(ref _discoveryLastPublishedAtUtc, value); }
    public string PairingState { get => _pairingState; private set => SetProperty(ref _pairingState, value); }
    public string PairingTimeRemaining { get => _pairingTimeRemaining; private set => SetProperty(ref _pairingTimeRemaining, value); }
    public string PairingDeviceName { get => _pairingDeviceName; private set => SetProperty(ref _pairingDeviceName, value); }
    public string PairingVerificationCode { get => _pairingVerificationCode; private set => SetProperty(ref _pairingVerificationCode, value); }
    public ImageSource? PairingQrImage { get => _pairingQrImage; private set => SetProperty(ref _pairingQrImage, value); }
    public bool IsPairingVisible { get => _isPairingVisible; private set => SetProperty(ref _isPairingVisible, value); }
#if DEBUG
    public bool IsDebugBuild => true;
#else
    public bool IsDebugBuild => false;
#endif

    private async Task LoadIdentityAsync()
    {
        WindowsDeviceId = await _identityProvider.GetOrCreateDeviceIdAsync();
        var settings = await _identityProvider.GetSettingsAsync();
        Port = settings.Port;
        UpdateNetworkDiagnostics(_addressProvider.GetPrimaryLocalIPv4Address(), settings.Port);
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

    private async Task LoadTrustedPhonesAsync()
    {
        var devices = await _trustedDeviceRepository.GetTrustedDevicesAsync();
        Dispatch(() =>
        {
            TrustedPhones.Clear();
            foreach (var device in devices)
            {
                TrustedPhones.Add(TrustedPhoneViewModel.From(device));
            }
        });
    }

    private async Task RevokeTrustedPhoneAsync(string deviceId)
    {
        if (ConnectedDeviceId == deviceId)
        {
            await _server.DisconnectActiveSessionAsync();
        }
        await _trustedDeviceRepository.RevokeAsync(deviceId, DateTimeOffset.UtcNow);
        await LoadTrustedPhonesAsync();
        Status = "Phone trust removed. New QR pairing is required.";
    }

    private async Task RestartDiscoveryAsync()
    {
        await _discoveryControl.RestartDiscoveryAsync();
        await LoadTrustedPhonesAsync();
    }

    private async Task StartPairingAsync()
    {
        if (!_server.IsRunning)
        {
            await _server.StartAsync();
        }

        var hostAddresses = _addressProvider.GetLocalIPv4Addresses();
        if (hostAddresses.Count == 0)
        {
            Status = "Не найден адрес локальной сети";
            PairingQrImage = null;
            _pairingQrPng = null;
            IsPairingVisible = false;
            UpdateNetworkDiagnostics(null, _server.Port);
            return;
        }

        UpdateNetworkDiagnostics(hostAddresses[0], _server.Port);
        var payload = await _pairingSessionManager.StartPairingAsync(_server.Port, hostAddresses);
        var content = JsonSerializer.Serialize(payload, ProtocolSerializerOptions.CamelCase);
        _pairingQrPng = _qrCodeGenerator.GeneratePng(content, 4);
        PairingQrImage = PngToImageSource(_pairingQrPng);
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
        _pairingQrPng = null;
        IsPairingVisible = false;
        _pairingTimer.Stop();
        await _discoveryControl.RestartDiscoveryAsync();
    }

    private Task ConfirmPairingCodeAsync()
    {
        _pairingSessionManager.ConfirmLocalUser();
        UpdatePairingState();
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
            UpdateNetworkDiagnostics(e.Service?.AdvertisedAddress ?? _addressProvider.GetPrimaryLocalIPv4Address(), e.Service?.Port ?? Port);
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
        if (_pairingSessionManager.State == DeviceSync.Application.PairingState.Completed)
        {
            _ = LoadTrustedPhonesAsync();
        }
        if (!string.IsNullOrWhiteSpace(_pairingSessionManager.CurrentSession?.VerificationCode))
        {
            PairingVerificationCode = FormatVerificationCode(_pairingSessionManager.CurrentSession.VerificationCode);
        }
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

    private async Task SavePairingQrAsync()
    {
#if DEBUG
        if (_pairingQrPng is null)
        {
            Status = "No QR image to save.";
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"devicesync-pairing-qr-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.png");
        await File.WriteAllBytesAsync(path, _pairingQrPng);
        Status = $"QR saved to {path}";
#else
        await Task.CompletedTask;
#endif
    }

    private static string FormatVerificationCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "-";
        }

        var padded = code.PadLeft(6, '0');
        return $"{padded[..3]} {padded[3..]}";
    }

    private void UpdateNetworkDiagnostics(string? address, int port)
    {
        AdvertisedAddress = string.IsNullOrWhiteSpace(address) ? "-" : address;
        TcpEndpoint = string.IsNullOrWhiteSpace(address) ? "-" : $"{address}:{port}";
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

public sealed record TrustedPhoneViewModel
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Fingerprint { get; init; }
    public required string PairedAtUtc { get; init; }
    public required string LastVerifiedAtUtc { get; init; }
    public required string TrustStatus { get; init; }

    public static TrustedPhoneViewModel From(TrustedDevice device)
    {
        return new TrustedPhoneViewModel
        {
            DeviceId = device.DeviceId,
            DeviceName = device.DeviceName,
            Fingerprint = Shorten(device.IdentityFingerprint),
            PairedAtUtc = device.PairedAtUtc.ToString("O"),
            LastVerifiedAtUtc = device.LastVerifiedAtUtc?.ToString("O") ?? "-",
            TrustStatus = device.TrustStatus,
        };
    }

    private static string Shorten(string value)
    {
        return value.Length <= 18 ? value : $"{value[..10]}...{value[^6..]}";
    }
}
