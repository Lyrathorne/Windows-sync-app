using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using DeviceSync.Application;
using Microsoft.Win32;

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
    private readonly OutgoingFileTransferManager _outgoingFileTransferManager;
    private readonly OutgoingTransferQueue _outgoingTransferQueue;
    private readonly SharingManager _sharingManager;
    private readonly NotificationManager _notificationManager;
    private readonly FolderSyncManager _folderSyncManager;
    private readonly WindowsStartupService _startupService;
    private readonly IncomingFileAutomationSettings _incomingFileSettings;
    private readonly System.Windows.Threading.DispatcherTimer _pairingTimer;
    private readonly System.Windows.Threading.DispatcherTimer _clipboardTimer;
    private string _status = "Starting";
    private string _serverSummary = "Stopped";
    private string _windowsDeviceId = "";
    private int _port = 54321;
    private string _connectedDeviceName = "No device connected";
    private string _connectedDeviceId = "-";
    private string _connectedCapabilities = "-";
    private string _connectedAtUtc = "-";
    private string _connectedTransport = "-";
    private string _discoverySummary = "Stopped";
    private string _discoveryInstanceName = "-";
    private string _discoveryServiceType = "_devicesync._tcp";
    private string _discoveryPublishedPort = "-";
    private string _advertisedAddress = "-";
    private string _tcpEndpoint = "-";
    private string _listeningEndpoint = "-";
    private string _networkInterfaceSummary = "-";
    private string _discoveryLastError = "-";
    private string _discoveryLastPublishedAtUtc = "-";
    private string _pairingState = "Disabled";
    private string _pairingTimeRemaining = "-";
    private string _pairingDeviceName = "-";
    private string _pairingVerificationCode = "-";
    private ImageSource? _pairingQrImage;
    private byte[]? _pairingQrPng;
    private bool _isPairingVisible;
    private bool _isPairingConfirmationVisible;
    private bool _isRetryStartVisible;
    private string _outgoingFileStatus = UiText("Loc.NoOutgoingTransfer", "No outgoing transfer");
    private double _outgoingFileProgress;
    private string _textToShare = "";
    private bool _clipboardSyncEnabled;
    private string _folderSyncStatus = "No folder sync active";
    private string _debugPairingPayload = "";
    private bool _startWithWindows;
    private string? _folderSyncId;
    private string _notificationStatus = "Notifications are limited to the current connection session.";

    public MainViewModel(
        IDeviceServer server,
        IServiceDiscoveryPublisher publisher,
        IDiscoveryControl discoveryControl,
        IPairingSessionManager pairingSessionManager,
        ITrustedDeviceRepository trustedDeviceRepository,
        IQrCodeGenerator qrCodeGenerator,
        ILocalNetworkAddressProvider addressProvider,
        IWindowsDeviceIdentityProvider identityProvider,
        OutgoingFileTransferManager outgoingFileTransferManager,
        OutgoingTransferQueue outgoingTransferQueue,
        SharingManager sharingManager,
        NotificationManager notificationManager,
        FolderSyncManager folderSyncManager,
        WindowsStartupService startupService,
        IncomingFileAutomationSettings incomingFileSettings)
    {
        _server = server;
        _publisher = publisher;
        _discoveryControl = discoveryControl;
        _pairingSessionManager = pairingSessionManager;
        _trustedDeviceRepository = trustedDeviceRepository;
        _qrCodeGenerator = qrCodeGenerator;
        _addressProvider = addressProvider;
        _identityProvider = identityProvider;
        _outgoingFileTransferManager = outgoingFileTransferManager;
        _outgoingTransferQueue = outgoingTransferQueue;
        _sharingManager = sharingManager;
        _notificationManager = notificationManager;
        _folderSyncManager = folderSyncManager;
        _startupService = startupService;
        _incomingFileSettings = incomingFileSettings;
        _startWithWindows = startupService.IsEnabled;
        _clipboardSyncEnabled = startupService.ClipboardEnabled;
        _sharingManager.ClipboardEnabled = _clipboardSyncEnabled;
        _sharingManager.IsDeviceAllowed = startupService.IsClipboardAllowedForDevice;
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
        SendFileCommand = new AsyncRelayCommand(SendFileAsync);
        CancelOutgoingFileCommand = new AsyncRelayCommand(() => _outgoingFileTransferManager.CancelAsync());
        SendClipboardCommand = new AsyncRelayCommand(SendClipboardAsync);
        ClearSharedTextHistoryCommand = new AsyncRelayCommand(() =>
        {
            _sharingManager.ClearHistory();
            SharedTextHistory.Clear();
            return Task.CompletedTask;
        });
        SendTextCommand = new AsyncRelayCommand(SendTextAsync);
        OpenSharedLinkCommand = new AsyncRelayCommand<SharedTextItem>(OpenSharedLinkAsync);
        ClearNotificationHistoryCommand = new AsyncRelayCommand(() =>
        {
            _notificationManager.ClearHistory();
            return Task.CompletedTask;
        });
        BlockNotificationAppCommand = new AsyncRelayCommand<ReceivedNotification>(notification =>
        {
            if (notification is not null)
            {
                _notificationManager.SetAppAllowed(notification.PackageName, false);
                if (!BlockedNotificationApps.Contains(notification.PackageName))
                    BlockedNotificationApps.Add(notification.PackageName);
                foreach (var item in Notifications.Where(item => item.PackageName == notification.PackageName).ToArray())
                    Notifications.Remove(item);
                NotificationStatus = $"{notification.AppName} notifications are blocked on this computer.";
            }
            return Task.CompletedTask;
        });
        UnblockNotificationAppCommand = new AsyncRelayCommand<string>(packageName =>
        {
            if (!string.IsNullOrWhiteSpace(packageName))
            {
                _notificationManager.SetAppAllowed(packageName, true);
                BlockedNotificationApps.Remove(packageName);
                NotificationStatus = $"{packageName} notifications are allowed on this computer.";
            }
            return Task.CompletedTask;
        });
        InvokeFirstNotificationActionCommand = new AsyncRelayCommand<ReceivedNotification>(InvokeFirstNotificationActionAsync);
        StartFolderSyncCommand = new AsyncRelayCommand(StartFolderSyncAsync);
        ApproveFolderSyncCommand = new AsyncRelayCommand(ApproveFolderSyncAsync);
        ExportSupportBundleCommand = new AsyncRelayCommand(ExportSupportBundleAsync);

        _server.StateChanged += OnServerStateChanged;
        _server.SessionChanged += OnSessionChanged;
        _publisher.StateChanged += OnPublisherStateChanged;
        _pairingSessionManager.StateChanged += OnPairingStateChanged;
        _outgoingFileTransferManager.Changed += OnOutgoingFileTransferChanged;
        _sharingManager.ItemReceived += item => Dispatch(() =>
        {
            foreach (var existing in SharedTextHistory
                .Where(existing => existing.ItemId == item.ItemId ||
                    string.Equals(existing.Text, item.Text, StringComparison.Ordinal))
                .ToArray())
            {
                SharedTextHistory.Remove(existing);
            }
            SharedTextHistory.Insert(0, item);
            while (SharedTextHistory.Count > 50) SharedTextHistory.RemoveAt(SharedTextHistory.Count - 1);
            Status = item.Kind == "url" ? "A link was added to Recent." : "Clipboard history updated.";
        });
        _sharingManager.ClipboardReceived += text => Dispatch(() => _ = SetClipboardWithRetryAsync(text));
        _notificationManager.Posted += notification => Dispatch(() =>
        {
            Notifications.Insert(0, notification);
            while (Notifications.Count > NotificationManager.MaximumHistory) Notifications.RemoveAt(Notifications.Count - 1);
            Status = $"{notification.AppName}: {notification.Title}";
            NotificationStatus = "A notification was received from Android.";
        });
        _notificationManager.Updated += notification => Dispatch(() =>
        {
            var existing = Notifications.FirstOrDefault(candidate => candidate.Key == notification.Key);
            if (existing is not null) Notifications.Remove(existing);
            Notifications.Insert(0, notification);
            NotificationStatus = "An Android notification was updated.";
        });
        _notificationManager.Removed += key => Dispatch(() =>
        {
            var item = Notifications.FirstOrDefault(candidate => candidate.Key == key);
            if (item is not null) Notifications.Remove(item);
        });
        _notificationManager.Cleared += () => Dispatch(Notifications.Clear);
        _notificationManager.ConnectionChanged += (_, _) => Dispatch(() =>
            NotificationStatus = _notificationManager.IsConnected
                ? "Connected. Waiting for allowed Android notifications."
                : "Android is offline. Session notification history was cleared.");
        _notificationManager.ActionResultReceived += result => Dispatch(() =>
            NotificationStatus = $"Android notification action result: {result}.");
        NotificationsView = CollectionViewSource.GetDefaultView(Notifications);
        NotificationsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ReceivedNotification.AppName)));
        foreach (var packageName in _notificationManager.Preferences.BlockedPackages.Order(StringComparer.Ordinal))
            BlockedNotificationApps.Add(packageName);
        _folderSyncManager.PlanCreated += plan => Dispatch(() =>
        {
            _folderSyncId = plan.SyncId;
            FolderConflicts.Clear();
            foreach (var conflict in plan.Operations.Where(operation => operation.Action == "conflict"))
                FolderConflicts.Add(new FolderConflictChoice(conflict.RelativePath));
            FolderSyncStatus = $"Plan: {plan.Operations.Count(operation => operation.Action == "upload")} upload, " +
                $"{plan.Operations.Count(operation => operation.Action == "download")} download, " +
                $"{FolderConflicts.Count} conflicts. Choose every conflict and approve.";
        });
        _folderSyncManager.StatusChanged += message => Dispatch(() => FolderSyncStatus = message);
        _pairingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _pairingTimer.Tick += (_, _) => UpdatePairingTimer();
        _clipboardTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _clipboardTimer.Tick += async (_, _) =>
        {
            if (!ClipboardSyncEnabled || !_sharingManager.IsCurrentDeviceAllowed) return;
            try
            {
                var text = await ReadClipboardTextWithRetryAsync();
                if (string.IsNullOrEmpty(text)) return;
                if (_sharingManager.ShouldSendLocalClipboard(text))
                {
                    try
                    {
                        await _sharingManager.SendClipboardAsync(text);
                    }
                    catch (Exception error)
                    {
                        _sharingManager.MarkLocalClipboardSendFailed(text);
                        Status = $"Automatic clipboard send failed: {error.Message}";
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another process temporarily owns the clipboard. The next timer tick retries.
            }
        };
        _clipboardTimer.Start();
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
    public ICommand SendFileCommand { get; }
    public ICommand CancelOutgoingFileCommand { get; }
    public ICommand SendClipboardCommand { get; }
    public ICommand ClearSharedTextHistoryCommand { get; }
    public ICommand SendTextCommand { get; }
    public ICommand OpenSharedLinkCommand { get; }
    public ICommand ClearNotificationHistoryCommand { get; }
    public ICommand BlockNotificationAppCommand { get; }
    public ICommand UnblockNotificationAppCommand { get; }
    public ICommand InvokeFirstNotificationActionCommand { get; }
    public ICommand StartFolderSyncCommand { get; }
    public ICommand ApproveFolderSyncCommand { get; }
    public ObservableCollection<TrustedPhoneViewModel> TrustedPhones { get; } = [];
    public ObservableCollection<SharedTextItem> SharedTextHistory { get; } = [];
    public ObservableCollection<ReceivedNotification> Notifications { get; } = [];
    public ObservableCollection<string> BlockedNotificationApps { get; } = [];
    public ICollectionView NotificationsView { get; }
    public ObservableCollection<FolderConflictChoice> FolderConflicts { get; } = [];
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string NotificationStatus { get => _notificationStatus; private set => SetProperty(ref _notificationStatus, value); }
    public bool NotificationToastsEnabled
    {
        get => _notificationManager.Preferences.ToastsEnabled;
        set
        {
            if (value == _notificationManager.Preferences.ToastsEnabled) return;
            _notificationManager.Preferences.ToastsEnabled = value;
            OnPropertyChanged();
        }
    }
    public bool HideSensitiveNotificationsWhenLocked
    {
        get => _notificationManager.Preferences.HideSensitiveWhenLocked;
        set
        {
            if (value == _notificationManager.Preferences.HideSensitiveWhenLocked) return;
            _notificationManager.Preferences.HideSensitiveWhenLocked = value;
            OnPropertyChanged();
        }
    }
    public string NotificationQuietHoursStart
    {
        get => _notificationManager.Preferences.QuietHoursStart?.ToString("HH:mm") ?? "";
        set
        {
            _notificationManager.Preferences.QuietHoursStart =
                TimeOnly.TryParse(value, out var parsed) ? parsed : null;
            OnPropertyChanged();
        }
    }
    public string NotificationQuietHoursEnd
    {
        get => _notificationManager.Preferences.QuietHoursEnd?.ToString("HH:mm") ?? "";
        set
        {
            _notificationManager.Preferences.QuietHoursEnd =
                TimeOnly.TryParse(value, out var parsed) ? parsed : null;
            OnPropertyChanged();
        }
    }
    public string ServerSummary { get => _serverSummary; private set => SetProperty(ref _serverSummary, value); }
    public ICommand ExportSupportBundleCommand { get; }
    public string WindowsDeviceId { get => _windowsDeviceId; private set => SetProperty(ref _windowsDeviceId, value); }
    public int Port { get => _port; private set => SetProperty(ref _port, value); }
    public string ConnectedDeviceName { get => _connectedDeviceName; private set => SetProperty(ref _connectedDeviceName, value); }
    public string ConnectedDeviceId
    {
        get => _connectedDeviceId;
        private set
        {
            if (SetProperty(ref _connectedDeviceId, value))
            {
                OnPropertyChanged(nameof(HasConnectedDevice));
                OnPropertyChanged(nameof(HasConnectedClipboardDevice));
                OnPropertyChanged(nameof(CanConfigureFileAutomation));
            }
        }
    }
    public string ConnectedCapabilities { get => _connectedCapabilities; private set => SetProperty(ref _connectedCapabilities, value); }
    public string ConnectedAtUtc { get => _connectedAtUtc; private set => SetProperty(ref _connectedAtUtc, value); }
    public string ConnectedTransport { get => _connectedTransport; private set => SetProperty(ref _connectedTransport, value); }
    public string DiscoverySummary { get => _discoverySummary; private set => SetProperty(ref _discoverySummary, value); }
    public string DiscoveryInstanceName { get => _discoveryInstanceName; private set => SetProperty(ref _discoveryInstanceName, value); }
    public string DiscoveryServiceType { get => _discoveryServiceType; private set => SetProperty(ref _discoveryServiceType, value); }
    public string DiscoveryPublishedPort { get => _discoveryPublishedPort; private set => SetProperty(ref _discoveryPublishedPort, value); }
    public string AdvertisedAddress { get => _advertisedAddress; private set => SetProperty(ref _advertisedAddress, value); }
    public string TcpEndpoint { get => _tcpEndpoint; private set => SetProperty(ref _tcpEndpoint, value); }
    public string ListeningEndpoint { get => _listeningEndpoint; private set => SetProperty(ref _listeningEndpoint, value); }
    public string NetworkInterfaceSummary { get => _networkInterfaceSummary; private set => SetProperty(ref _networkInterfaceSummary, value); }
    public string DiscoveryLastError { get => _discoveryLastError; private set => SetProperty(ref _discoveryLastError, value); }
    public string DiscoveryLastPublishedAtUtc { get => _discoveryLastPublishedAtUtc; private set => SetProperty(ref _discoveryLastPublishedAtUtc, value); }
    public string PairingState { get => _pairingState; private set => SetProperty(ref _pairingState, value); }
    public string PairingTimeRemaining { get => _pairingTimeRemaining; private set => SetProperty(ref _pairingTimeRemaining, value); }
    public string PairingDeviceName { get => _pairingDeviceName; private set => SetProperty(ref _pairingDeviceName, value); }
    public string PairingVerificationCode { get => _pairingVerificationCode; private set => SetProperty(ref _pairingVerificationCode, value); }
    public ImageSource? PairingQrImage { get => _pairingQrImage; private set => SetProperty(ref _pairingQrImage, value); }
    public bool IsPairingVisible { get => _isPairingVisible; private set => SetProperty(ref _isPairingVisible, value); }
    public bool IsPairingConfirmationVisible { get => _isPairingConfirmationVisible; private set => SetProperty(ref _isPairingConfirmationVisible, value); }
    public bool IsRetryStartVisible { get => _isRetryStartVisible; private set => SetProperty(ref _isRetryStartVisible, value); }
    public string OutgoingFileStatus { get => _outgoingFileStatus; private set => SetProperty(ref _outgoingFileStatus, value); }
    public double OutgoingFileProgress { get => _outgoingFileProgress; private set => SetProperty(ref _outgoingFileProgress, value); }
    public string TextToShare { get => _textToShare; set => SetProperty(ref _textToShare, value); }
    public bool ClipboardSyncEnabled
    {
        get => _clipboardSyncEnabled;
        set
        {
            if (SetProperty(ref _clipboardSyncEnabled, value))
            {
                _sharingManager.ClipboardEnabled = value;
                _startupService.ClipboardEnabled = value;
            }
        }
    }
    public bool HasConnectedDevice => !string.IsNullOrWhiteSpace(ConnectedDeviceId) && ConnectedDeviceId != "-";
    public bool HasConnectedClipboardDevice => HasConnectedDevice;
    public bool ClipboardDeviceEnabled
    {
        get => HasConnectedClipboardDevice && _startupService.IsClipboardAllowedForDevice(ConnectedDeviceId);
        set
        {
            if (!HasConnectedClipboardDevice) return;
            _startupService.SetClipboardAllowedForDevice(ConnectedDeviceId, value);
            OnPropertyChanged();
        }
    }
    public bool AutomaticFileReceiveEnabled
    {
        get => _incomingFileSettings.AutomaticReceiveEnabled;
        set { _incomingFileSettings.AutomaticReceiveEnabled = value; OnPropertyChanged(); }
    }
    public bool CanConfigureFileAutomation => HasConnectedClipboardDevice;
    public int IncomingFileReceiveModeIndex
    {
        get => (int)CurrentIncomingFilePolicy.Mode;
        set
        {
            if (!CanConfigureFileAutomation || !Enum.IsDefined(typeof(IncomingFileReceiveMode), value)) return;
            SaveIncomingFilePolicy(CurrentIncomingFilePolicy with { Mode = (IncomingFileReceiveMode)value });
            OnPropertyChanged();
        }
    }
    public double IncomingFileAutomaticLimitMb
    {
        get => Math.Round(CurrentIncomingFilePolicy.AutomaticLimitBytes / 1024d / 1024d, 1);
        set
        {
            if (!CanConfigureFileAutomation || double.IsNaN(value)) return;
            var bytes = (long)(Math.Clamp(value, 0, 100) * 1024 * 1024);
            SaveIncomingFilePolicy(CurrentIncomingFilePolicy with { AutomaticLimitBytes = bytes });
            OnPropertyChanged();
        }
    }
    public bool IncomingFileTrustedNetworkOnly
    {
        get => CurrentIncomingFilePolicy.TrustedNetworkOnly;
        set
        {
            if (!CanConfigureFileAutomation) return;
            SaveIncomingFilePolicy(CurrentIncomingFilePolicy with { TrustedNetworkOnly = value });
            OnPropertyChanged();
        }
    }
    public bool CurrentNetworkTrusted
    {
        get => _incomingFileSettings.IsCurrentNetworkTrusted;
        set
        {
            if (_incomingFileSettings.SetCurrentNetworkTrusted(value)) OnPropertyChanged();
        }
    }
    private IncomingFileAutomationPolicy CurrentIncomingFilePolicy =>
        CanConfigureFileAutomation ? _incomingFileSettings.GetPolicy(ConnectedDeviceId) : new();
    private void SaveIncomingFilePolicy(IncomingFileAutomationPolicy policy) =>
        _incomingFileSettings.SetPolicy(ConnectedDeviceId, policy);
    public string FolderSyncStatus { get => _folderSyncStatus; private set => SetProperty(ref _folderSyncStatus, value); }
    public string DebugPairingPayload { get => _debugPairingPayload; private set => SetProperty(ref _debugPairingPayload, value); }
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { if (SetProperty(ref _startWithWindows, value)) _startupService.SetEnabled(value); }
    }
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

    private Task ExportSupportBundleAsync()
    {
        var preview =
            $"Version: {typeof(MainViewModel).Assembly.GetName().Version}\n" +
            $"Server: {DiagnosticRedactor.SanitizeValue(ServerSummary)}\n" +
            $"Transport: {DiagnosticRedactor.SanitizeValue(ConnectedTransport)}\n" +
            $"Capabilities: {DiagnosticRedactor.SanitizeValue(ConnectedCapabilities)}\n\n" +
            "The ZIP excludes clipboard/file/notification contents, private paths, secrets and keys.";
        if (System.Windows.MessageBox.Show(
                preview,
                "Privacy-safe support bundle preview",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK)
            return Task.CompletedTask;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export privacy-safe DeviceSync support bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"devicesync-support-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        var summary = new Dictionary<string, string>
        {
            ["version"] = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["server"] = ServerSummary,
            ["transport"] = ConnectedTransport,
            ["capabilities"] = ConnectedCapabilities,
            ["discovery"] = DiscoverySummary,
            ["os"] = Environment.OSVersion.VersionString,
            ["runtime"] = Environment.Version.ToString(),
        };
        File.WriteAllBytes(dialog.FileName, PrivacySafeDiagnostics.BuildSupportBundle(summary));
        Status = "Privacy-safe support bundle exported.";
        return Task.CompletedTask;
    }

    private async Task StartAsync()
    {
        Status = "Запуск сервера...";
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

    private async Task SendFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Multiselect = false, Title = "Send file to Android" };
        if (dialog.ShowDialog() != true) return;
        await QueueFileAsync(dialog.FileName);
    }

    public async Task QueueFileAsync(string filePath)
    {
        try
        {
            await _outgoingTransferQueue.EnqueueAsync(filePath);
            OutgoingFileStatus = $"Queued: {Path.GetFileName(filePath)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            OutgoingFileStatus = error.Message;
        }
    }

    private void OnOutgoingFileTransferChanged(object? sender, OutgoingFileTransferChangedEventArgs args)
    {
        Dispatch(() =>
        {
            var transfer = args.Transfer;
            OutgoingFileProgress = transfer.SizeBytes == 0 ? 100 : transfer.SentBytes * 100d / transfer.SizeBytes;
            OutgoingFileStatus = $"{transfer.FileName}: {transfer.State} — {transfer.SentBytes:N0}/{transfer.SizeBytes:N0} bytes" +
                (args.BytesPerSecond > 0 ? $" — {args.BytesPerSecond:N0} B/s" : string.Empty) +
                (string.IsNullOrWhiteSpace(transfer.Error) ? string.Empty : $" — {transfer.Error}");
        });
    }

    private async Task SendClipboardAsync()
    {
        var text = await ReadClipboardTextWithRetryAsync();
        if (string.IsNullOrEmpty(text))
        {
            Status = "Clipboard does not contain readable text.";
            return;
        }

        try
        {
            await _sharingManager.SendClipboardNowAsync(text);
            Status = $"Clipboard text sent ({System.Text.Encoding.UTF8.GetByteCount(text):N0} bytes).";
        }
        catch (Exception error)
        {
            Status = $"Clipboard send failed: {error.Message}";
        }
    }

    private static async Task<string?> ReadClipboardTextWithRetryAsync()
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                if (data is null) return null;

                // Respect the standard Windows clipboard privacy markers used by password
                // managers and apps that opt out of history/cloud clipboard processing.
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) return null;
                if (data.GetDataPresent("CanIncludeInClipboardHistory", false) &&
                    data.GetData("CanIncludeInClipboardHistory", false) is bool canInclude && !canInclude)
                    return null;

                string[] preferredFormats =
                [
                    System.Windows.DataFormats.UnicodeText,
                    System.Windows.DataFormats.Text,
                    System.Windows.DataFormats.StringFormat,
                    System.Windows.DataFormats.OemText,
                ];
                foreach (var format in preferredFormats)
                {
                    if (data.GetDataPresent(format, autoConvert: true) &&
                        data.GetData(format, autoConvert: true) is string text &&
                        text.Length > 0)
                    {
                        return text;
                    }
                }

                return null;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                await Task.Delay(attempt * 100);
            }
        }

        return null;
    }

    private async Task SetClipboardWithRetryAsync(string text)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                await Task.Delay(attempt * 100);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Status = "Windows clipboard is busy. The received text was not applied.";
            }
        }
    }

    private async Task SendTextAsync()
    {
        var text = TextToShare.Trim();
        if (text.Length == 0)
        {
            Status = "Enter text or an HTTPS link first.";
            return;
        }

        try
        {
            await _sharingManager.SendTextAsync(text);
            TextToShare = "";
            Status = $"Text sent ({System.Text.Encoding.UTF8.GetByteCount(text):N0} bytes).";
        }
        catch (Exception error)
        {
            Status = $"Text send failed: {error.Message}";
        }
    }

    private Task OpenSharedLinkAsync(SharedTextItem? item)
    {
        if (item?.Kind == "url" && Uri.TryCreate(item.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        else if (item is not null)
            System.Windows.Clipboard.SetText(item.Text);
        return Task.CompletedTask;
    }

    private async Task InvokeFirstNotificationActionAsync(ReceivedNotification? notification)
    {
        var action = notification?.Actions.FirstOrDefault(candidate => !candidate.IsDestructive);
        if (notification is null || action is null)
        {
            NotificationStatus = "This notification has no safe remote action.";
            return;
        }
        var answer = System.Windows.MessageBox.Show(
            $"Run “{action.Title}” for the notification from {notification.AppName}?\n\n" +
            "DeviceSync will send only an opaque action token to the paired phone.",
            "Confirm notification action",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            NotificationStatus = "Notification action cancelled.";
            return;
        }
        try
        {
            await _notificationManager.InvokeActionAsync(notification, action, confirmedByUser: true);
            NotificationStatus = "Action request sent to Android.";
        }
        catch (Exception error)
        {
            NotificationStatus = $"Notification action failed: {error.Message}";
        }
    }

    private async Task StartFolderSyncAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select an explicitly shared folder", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        var syncId = await _folderSyncManager.StartAsync(dialog.FolderName);
        FolderSyncStatus = $"Manifest sent ({syncId}). Waiting for remote manifest; no files have been changed.";
    }

    private async Task ApproveFolderSyncAsync()
    {
        if (_folderSyncId is null) return;
        if (FolderConflicts.Any(item => string.IsNullOrWhiteSpace(item.Resolution)))
        {
            FolderSyncStatus = "Choose a resolution for every conflict before approval.";
            return;
        }
        await _folderSyncManager.ApproveAsync(_folderSyncId,
            FolderConflicts.ToDictionary(item => item.RelativePath, item => item.Resolution!, StringComparer.Ordinal));
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
            IsPairingConfirmationVisible = false;
            UpdateNetworkDiagnostics(null, _server.Port);
            return;
        }

        UpdateNetworkDiagnostics(hostAddresses[0], _server.Port);
        var payload = await _pairingSessionManager.StartPairingAsync(_server.Port, hostAddresses);
        var content = BuildCompactPairingQrPayload(payload);
#if DEBUG
        DebugPairingPayload = content;
#endif
        _pairingQrPng = _qrCodeGenerator.GeneratePng(content, 4);
        var qrSize = ReadPngSize(_pairingQrPng);
        Debug.WriteLine($"DeviceSync QR payload byte length: {System.Text.Encoding.UTF8.GetByteCount(content)}");
        Debug.WriteLine($"DeviceSync QR PNG width/height: {qrSize.Width}x{qrSize.Height}");
        PairingQrImage = PngToImageSource(_pairingQrPng);
        PairingDeviceName = payload.WindowsDeviceName;
        PairingVerificationCode = "-";
        IsPairingConfirmationVisible = false;
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
        DebugPairingPayload = "";
        IsPairingVisible = false;
        IsPairingConfirmationVisible = false;
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
        PairingState = UiText("Loc.Pairing.Rejected", "The codes did not match.");
    }

    private void OnServerStateChanged(object? sender, ServerStateChangedEventArgs e)
    {
        Dispatch(() =>
        {
            Port = e.Port;
            ServerSummary = e.IsRunning ? $"Listening on TCP {e.Port}" : "Ошибка запуска";
            Status = e.IsRunning ? "Готово к подключению" : "Ошибка запуска";
            IsRetryStartVisible = !e.IsRunning;
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
                ConnectedTransport = "-";
                OnPropertyChanged(nameof(HasConnectedClipboardDevice));
                OnPropertyChanged(nameof(ClipboardDeviceEnabled));
                NotifyIncomingFilePolicyChanged();
                return;
            }

            ConnectedDeviceName = e.Session.DeviceName;
            ConnectedDeviceId = e.Session.DeviceId;
            ConnectedCapabilities = string.Join(", ", e.Session.Capabilities);
            ConnectedAtUtc = e.Session.ConnectedAtUtc.ToString("O");
            ConnectedTransport = e.Session.IsSlowTransport
                ? "Bluetooth RFCOMM — slow secure fallback"
                : e.Session.TransportKind switch
                {
                    DeviceTransportKind.UsbTethering => "USB tethering — preferred",
                    DeviceTransportKind.Hotspot => "Local hotspot",
                    _ => "LAN / Wi-Fi",
                };
            Status = e.Session.IsSlowTransport
                ? "Connected through slow Bluetooth fallback. Large files and media catalog are disabled."
                : $"Connected through {ConnectedTransport}.";
            OnPropertyChanged(nameof(HasConnectedClipboardDevice));
            OnPropertyChanged(nameof(ClipboardDeviceEnabled));
            NotifyIncomingFilePolicyChanged();
            _ = _sharingManager.FlushPendingClipboardAsync();
        });
    }

    private void NotifyIncomingFilePolicyChanged()
    {
        OnPropertyChanged(nameof(CanConfigureFileAutomation));
        OnPropertyChanged(nameof(IncomingFileReceiveModeIndex));
        OnPropertyChanged(nameof(IncomingFileAutomaticLimitMb));
        OnPropertyChanged(nameof(IncomingFileTrustedNetworkOnly));
        OnPropertyChanged(nameof(CurrentNetworkTrusted));
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
        PairingState = PairingStateText(_pairingSessionManager.State);
        if (_pairingSessionManager.State == DeviceSync.Application.PairingState.Completed)
        {
            _ = LoadTrustedPhonesAsync();
        }
        if (!string.IsNullOrWhiteSpace(_pairingSessionManager.CurrentSession?.VerificationCode))
        {
            PairingVerificationCode = FormatVerificationCode(_pairingSessionManager.CurrentSession.VerificationCode);
            IsPairingConfirmationVisible = true;
        }
        else if (_pairingSessionManager.CurrentSession is not null)
        {
            PairingVerificationCode = "-";
            IsPairingConfirmationVisible = false;
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
            PairingTimeRemaining = UiText("Loc.Pairing.ExpiredShort", "expired");
            PairingState = UiText("Loc.Pairing.Expired", "The pairing code expired.");
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

    private static string PairingStateText(DeviceSync.Application.PairingState state)
    {
        return state switch
        {
            DeviceSync.Application.PairingState.Starting => UiText("Loc.Pairing.Starting", "Creating a pairing code..."),
            DeviceSync.Application.PairingState.WaitingForDevice => UiText("Loc.Pairing.Waiting", "Waiting for the phone to scan the QR code..."),
            DeviceSync.Application.PairingState.ProofVerified => UiText("Loc.Pairing.Verifying", "Phone found. Verifying the secure connection..."),
            DeviceSync.Application.PairingState.WaitingForUserConfirmation => UiText("Loc.Pairing.Compare", "Compare the verification code on both devices."),
            DeviceSync.Application.PairingState.Completing => UiText("Loc.Pairing.Completing", "Finishing phone setup..."),
            DeviceSync.Application.PairingState.Completed => UiText("Loc.Pairing.Completed", "Phone connected successfully."),
            DeviceSync.Application.PairingState.Expired => UiText("Loc.Pairing.Expired", "The pairing code expired."),
            DeviceSync.Application.PairingState.Rejected => UiText("Loc.Pairing.Rejected", "The codes did not match."),
            _ => state.ToString(),
        };
    }

    private static string UiText(string resourceKey, string fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as string ?? fallback;

    private void UpdateNetworkDiagnostics(string? address, int port)
    {
        AdvertisedAddress = string.IsNullOrWhiteSpace(address) ? "-" : address;
        TcpEndpoint = string.IsNullOrWhiteSpace(address) ? "-" : $"{address}:{port}";
        ListeningEndpoint = $"0.0.0.0:{port}";
        var endpoints = _addressProvider.GetCandidateEndpoints(port);
        NetworkInterfaceSummary = endpoints.Count == 0
            ? "No private or link-local interfaces"
            : string.Join(", ", endpoints
                .GroupBy(endpoint => endpoint.Kind)
                .Select(group => $"{group.Key}: {group.Count()}"));
    }

    private static string BuildCompactPairingQrPayload(PairingQrPayload payload)
    {
        var compact = new Dictionary<string, object?>
        {
            ["f"] = payload.Format,
            ["v"] = payload.Version,
            ["sid"] = payload.SessionId,
            ["sec"] = payload.PairingSecret,
            ["exp"] = payload.ExpiresAtUtc,
            ["h"] = payload.HostAddresses,
            ["p"] = payload.Port,
            ["did"] = payload.WindowsDeviceId,
            ["dn"] = payload.WindowsDeviceName,
            ["pk"] = payload.WindowsIdentityPublicKey,
            ["fp"] = payload.WindowsIdentityFingerprint,
            ["tlsfp"] = payload.TlsServerSpkiFingerprint,
            ["pmin"] = payload.ProtocolMin,
            ["pmax"] = payload.ProtocolMax,
        };
        return JsonSerializer.Serialize(compact, ProtocolSerializerOptions.CamelCase);
    }

    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        return (ReadBigEndianInt32(png, 16), ReadBigEndianInt32(png, 20));
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) |
            (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) |
            bytes[offset + 3];
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

public sealed class FolderConflictChoice : ObservableObject
{
    private string? _resolution;
    public FolderConflictChoice(string relativePath) => RelativePath = relativePath;
    public string RelativePath { get; }
    public IReadOnlyList<string> Options { get; } =
        [FolderConflictResolutions.KeepWindows, FolderConflictResolutions.KeepAndroid, FolderConflictResolutions.KeepBoth];
    public string? Resolution { get => _resolution; set => SetProperty(ref _resolution, value); }
}
