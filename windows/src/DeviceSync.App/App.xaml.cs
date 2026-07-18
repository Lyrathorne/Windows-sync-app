using System.Net.Sockets;
using System.Windows;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Windows.Markup;

namespace DeviceSync.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\DeviceSync.App.SingleInstance";
    private const string SingleInstanceActivationEventName = @"Local\DeviceSync.App.Activate";

    private IHost? _host;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _singleInstanceActivationEvent;
    private CancellationTokenSource? _activationListenerCancellation;
    private bool _ownsSingleInstanceMutex;
    private bool _exitRequested;
    private MainWindow? _mainWindow;
    private FilesWindow? _filesWindow;
    private EdgePanelWindow? _edgePanelWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyCultureResources();
        ApplyWindowsTheme();
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(SingleInstanceActivationEventName);
                activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The primary instance is still starting. A later tray or hotkey action can reveal it.
            }
            Shutdown();
            return;
        }
        _singleInstanceActivationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            SingleInstanceActivationEventName);

        try
        {
            await StartApplicationAsync();
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ShowStartupError(
                "DeviceSync cannot start because its network port is already in use. " +
                "An older DeviceSync build may still be running in the notification area. " +
                "Exit it from the tray and start this build again.");
        }
        catch (Exception exception)
        {
            PrivacySafeDiagnostics.Record(new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                "application",
                "STARTUP_FAILED",
                "error",
                ErrorCode: exception.GetType().Name));
#if DEBUG
            ShowStartupError($"DeviceSync could not start.\n\n{exception}");
#else
            ShowStartupError($"DeviceSync could not start.\n\n{exception.Message}");
#endif
        }
    }

    private async Task StartApplicationAsync()
    {
        PrivacySafeDiagnostics.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceSync",
            "Diagnostics"));
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            PrivacySafeDiagnostics.Record(new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                "application",
                "APPLICATION_CRASH",
                "error",
                ErrorCode: (args.ExceptionObject as Exception)?.GetType().Name ?? "UNKNOWN"));
        DispatcherUnhandledException += (_, args) =>
            PrivacySafeDiagnostics.Record(new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                "application",
                "UI_THREAD_CRASH",
                "error",
                ErrorCode: args.Exception.GetType().Name));
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
#if DEBUG
                logging.AddDebug();
                logging.AddConsole();
#endif
                logging.AddProvider(new PrivacySafeLoggerProvider());
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IWindowsDeviceIdentityProvider, FileWindowsDeviceIdentityProvider>();
                services.AddSingleton<IProtectedKeyStorage, FileProtectedKeyStorage>();
                services.AddSingleton<IDataProtector, WindowsDataProtector>();
                services.AddSingleton<WindowsDeviceIdentityKeyProvider>();
                services.AddSingleton<IDeviceIdentityKeyProvider>(sp => sp.GetRequiredService<WindowsDeviceIdentityKeyProvider>());
                services.AddSingleton<ITlsCertificateProvider>(sp => sp.GetRequiredService<WindowsDeviceIdentityKeyProvider>());
                services.AddSingleton<IPairingSessionManager, PairingSessionManager>();
                services.AddSingleton<ITrustedDeviceRepository, FileTrustedDeviceRepository>();
                services.AddSingleton<IQrCodeGenerator, QrCodeGenerator>();
                services.AddSingleton<ILocalNetworkAddressProvider, LocalNetworkAddressProvider>();
                services.AddSingleton<ILanBeaconPublisher, LanBeaconPublisher>();
                services.AddSingleton<DeviceSessionRegistry>();
                services.AddSingleton<TransportSessionCoordinator>();
                services.AddSingleton<IIncomingFileStorage, WindowsIncomingFileStorage>();
                services.AddSingleton<IIncomingTransferCheckpointStore, JsonIncomingTransferCheckpointStore>();
                services.AddSingleton<IncomingFileTransferDecisionCoordinator>();
                services.AddSingleton<IncomingFileAutomationSettings>();
                services.AddSingleton<IIncomingFilePolicyStore>(sp => sp.GetRequiredService<IncomingFileAutomationSettings>());
                services.AddSingleton<IIncomingFileNetworkContext>(sp => sp.GetRequiredService<IncomingFileAutomationSettings>());
                services.AddSingleton<SecureIncomingFileDecisionService>(sp => new(
                    sp.GetRequiredService<IncomingFileTransferDecisionCoordinator>(),
                    sp.GetRequiredService<ITrustedDeviceRepository>(),
                    sp.GetRequiredService<IIncomingFilePolicyStore>(),
                    sp.GetRequiredService<IIncomingFileNetworkContext>()));
                services.AddSingleton<IIncomingFileTransferDecisionService>(sp => sp.GetRequiredService<SecureIncomingFileDecisionService>());
                services.AddSingleton<IIncomingFileTransferGuard>(sp => sp.GetRequiredService<SecureIncomingFileDecisionService>());
                services.AddSingleton<IncomingFileTransferManager>();
                services.AddSingleton<WindowsFileTransferTransport>();
                services.AddSingleton<IOutgoingFileTransferTransport>(sp => sp.GetRequiredService<WindowsFileTransferTransport>());
                services.AddSingleton<OutgoingFileTransferManager>();
                services.AddSingleton<IOutgoingTransferQueueStore, JsonOutgoingTransferQueueStore>();
                services.AddSingleton<OutgoingTransferQueue>();
                services.AddSingleton<FeatureMessageTransport>();
                services.AddSingleton<IFeatureMessageTransport>(sp => sp.GetRequiredService<FeatureMessageTransport>());
                services.AddSingleton<IMediaThumbnailCache, WindowsMediaThumbnailCache>();
                services.AddSingleton<CatalogDownloadAuthorizer>();
                services.AddSingleton<MediaCatalogClientFactory>();
                services.AddSingleton<IPrivacyShieldMonitor, WindowsPrivacyShieldMonitor>();
                services.AddSingleton<SharingManager>();
                services.AddSingleton<INotificationPreferences, JsonNotificationPreferences>();
                services.AddSingleton<NotificationManager>();
                services.AddSingleton<IFolderManifestBuilder, WindowsFolderManifestBuilder>();
                services.AddSingleton<IFolderSyncRootStore, JsonFolderSyncRootStore>();
                services.AddSingleton<FolderSyncManager>();
                services.AddSingleton<IFolderFileTransferAuthorizer>(sp => new CompositeFileTransferAuthorizer(
                    sp.GetRequiredService<FolderSyncManager>(),
                    sp.GetRequiredService<CatalogDownloadAuthorizer>()));
                services.AddSingleton<TcpDeviceServer>();
                services.AddSingleton<BluetoothRfcommDeviceServer>();
                services.AddSingleton<IServiceDiscoveryPublisher, SimpleMdnsServiceDiscoveryPublisher>();
                services.AddSingleton<DeviceServerManager>();
                services.AddSingleton<IDeviceServer>(sp => sp.GetRequiredService<DeviceServerManager>());
                services.AddSingleton<IDiscoveryControl>(sp => sp.GetRequiredService<DeviceServerManager>());
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<EdgePanelViewModel>();
                services.AddSingleton<IEdgePanelSettingsStore, JsonEdgePanelSettingsStore>();
                services.AddSingleton<IEdgePanelPlacementService, EdgePanelPlacementService>();
                services.AddSingleton<WindowsStartupService>();
                services.AddSingleton<IncomingFileViewModel>();
                services.AddSingleton<FilesViewModel>();
                services.AddSingleton<RecentFilesViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<EdgePanelWindow>();
            })
            .Build();

        await _host.StartAsync();
        await _host.Services.GetRequiredService<IncomingFileTransferManager>().CleanupStalePartialsAsync();
        await _host.Services.GetRequiredService<FolderSyncManager>().InitializeAsync();
        await _host.Services.GetRequiredService<OutgoingTransferQueue>().StartAsync();

        var edgePanel = _host.Services.GetRequiredService<EdgePanelWindow>();
        _edgePanelWindow = edgePanel;
        await edgePanel.InitializeAsync();
        var startupPlan = DeviceSyncStartupPolicy.Create(edgePanel.Shell.Enabled);
        edgePanel.Shell.SetState(startupPlan.InitialPanelState);
        edgePanel.OpenDiagnosticsRequested += (_, _) => ShowDiagnosticsWindow();
        edgePanel.OpenPhoneFilesRequested += (_, _) => ShowPhoneFilesWindow();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DeviceSync",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
        };
        _trayIcon.ContextMenuStrip.Items.Add(T("Loc.Tray.Open"), null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await edgePanel.OpenAsync(activate: true)));
        _trayIcon.ContextMenuStrip.Items.Add(T("Loc.Tray.TogglePanel"), null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await edgePanel.ToggleExpandedAsync(activate: true)));
        var edgeEnabledItem = new System.Windows.Forms.ToolStripMenuItem(T("Loc.Tray.EnablePanel"))
        {
            Checked = edgePanel.Shell.Enabled,
            CheckOnClick = true,
        };
        edgeEnabledItem.CheckedChanged += async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await edgePanel.SetEnabledAsync(edgeEnabledItem.Checked));
        edgePanel.Shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EdgePanelViewModel.Enabled) && edgeEnabledItem.Checked != edgePanel.Shell.Enabled)
            {
                edgeEnabledItem.Checked = edgePanel.Shell.Enabled;
            }
        };
        _trayIcon.ContextMenuStrip.Items.Add(edgeEnabledItem);
        var edgeMenu = new System.Windows.Forms.ToolStripMenuItem(T("Loc.Tray.PanelEdge"));
        edgeMenu.DropDownItems.Add(T("Loc.Left"), null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await edgePanel.SetSideAsync(EdgePanelSide.Left)));
        edgeMenu.DropDownItems.Add(T("Loc.Right"), null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await edgePanel.SetSideAsync(EdgePanelSide.Right)));
        _trayIcon.ContextMenuStrip.Items.Add(edgeMenu);
        var monitorMenu = new System.Windows.Forms.ToolStripMenuItem(T("Loc.Tray.PanelMonitor"));
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var deviceName = screen.DeviceName;
            var label = screen.Primary ? $"{deviceName} ({T("Loc.Tray.Primary")})" : deviceName;
            monitorMenu.DropDownItems.Add(label, null, async (_, _) =>
                await Dispatcher.InvokeAsync(async () => await edgePanel.SetMonitorAsync(deviceName)));
        }
        _trayIcon.ContextMenuStrip.Items.Add(monitorMenu);
        _trayIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(T("Loc.Tray.Diagnostics"), null, (_, _) => Dispatcher.Invoke(ShowDiagnosticsWindow));
        _trayIcon.ContextMenuStrip.Items.Add(T("Loc.Tray.Exit"), null, (_, _) => Dispatcher.Invoke(() => { _exitRequested = true; Shutdown(); }));
        _trayIcon.DoubleClick += async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await edgePanel.OpenAsync(activate: true));
        _trayIcon.BalloonTipClicked += async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await edgePanel.OpenAsync(activate: true));
        var notificationManager = _host.Services.GetRequiredService<NotificationManager>();
        var privacyShield = _host.Services.GetRequiredService<IPrivacyShieldMonitor>();
        notificationManager.Posted += notification => Dispatcher.Invoke(() =>
        {
            if (!notificationManager.ShouldShowSystemNotification(notification, privacyShield.IsSensitiveSession)) return;
            _trayIcon?.ShowBalloonTip(
                5000,
                $"{notification.AppName}: {notification.DisplayTitle}",
                notification.Text,
                System.Windows.Forms.ToolTipIcon.Info);
        });
        _host.Services.GetRequiredService<IncomingFileTransferManager>().TransferChanged += (_, args) =>
        {
            if (args.Transfer.State != IncomingFileTransferState.Completed) return;
            Dispatcher.Invoke(() => _trayIcon?.ShowBalloonTip(
                5000,
                T("Loc.FileReceivedTitle"),
                string.Format(T("Loc.FileReceivedBody"), args.Transfer.SafeFileName),
                System.Windows.Forms.ToolTipIcon.Info));
        };
        if (startupPlan.InitialPanelState != EdgePanelState.Hidden)
            await edgePanel.ShowPanelAsync(activate: false);
        StartActivationListener(edgePanel);

        var server = _host.Services.GetRequiredService<IDeviceServer>();
        await server.StartAsync();
    }

    private void ShowDiagnosticsWindow()
    {
        if (_host is null) return;
        if (_mainWindow is null)
        {
            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _mainWindow.Title = T("Loc.Diagnostics.Title");
            _mainWindow.Closing += (_, args) =>
            {
                if (_exitRequested) return;
                args.Cancel = true;
                _mainWindow.Hide();
            };
        }
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ShowPhoneFilesWindow()
    {
        if (_host is null || _edgePanelWindow is null) return;
        if (_filesWindow is null)
        {
            _filesWindow = new FilesWindow(_host.Services.GetRequiredService<FilesViewModel>());
            _filesWindow.Closed += (_, _) =>
            {
                _edgePanelWindow?.SetChildInteractionActive(false);
                _filesWindow = null;
            };
        }
        _edgePanelWindow.SetChildInteractionActive(true);
        _filesWindow.Show();
        if (_filesWindow.WindowState == WindowState.Minimized) _filesWindow.WindowState = WindowState.Normal;
        _filesWindow.Activate();
    }

    private void StartActivationListener(EdgePanelWindow edgePanel)
    {
        if (_singleInstanceActivationEvent is null) return;
        _activationListenerCancellation = new CancellationTokenSource();
        var cancellation = _activationListenerCancellation;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _singleInstanceActivationEvent, cancellation.Token.WaitHandle };
            while (!cancellation.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) != 0) break;
                _ = Dispatcher.InvokeAsync(async () => await edgePanel.OpenAsync(activate: true));
            }
        }, cancellation.Token);
    }

    private void ApplyCultureResources()
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        var dictionaries = Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null) dictionaries.Remove(existing);
        dictionaries.Add(new ResourceDictionary { Source = new Uri($"Resources/Strings.{language}.xaml", UriKind.Relative) });
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag)));
    }

    private string T(string key) => TryFindResource(key) as string ?? key;

    private void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.Invoke(ApplyWindowsTheme);
    }

    private void ApplyWindowsTheme()
    {
        var dictionaries = Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Themes/DesignTokens.", StringComparison.OrdinalIgnoreCase) == true);
        var target = SystemParameters.HighContrast
            ? "Themes/DesignTokens.HighContrast.xaml"
            : "Themes/DesignTokens.Light.xaml";
        if (existing?.Source?.OriginalString.EndsWith(target, StringComparison.OrdinalIgnoreCase) == true) return;
        if (existing is not null) dictionaries.Remove(existing);
        dictionaries.Insert(0, new ResourceDictionary { Source = new Uri(target, UriKind.Relative) });
    }

    private void ShowStartupError(string message)
    {
        _exitRequested = true;
        if (_edgePanelWindow is not null)
        {
            _edgePanelWindow.AllowClose();
            _edgePanelWindow.Close();
            _edgePanelWindow = null;
        }
        System.Windows.MessageBox.Show(
            message,
            "DeviceSync startup error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _exitRequested = true;
        _activationListenerCancellation?.Cancel();
        _activationListenerCancellation?.Dispose();
        _activationListenerCancellation = null;
        _singleInstanceActivationEvent?.Dispose();
        _singleInstanceActivationEvent = null;
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        if (_edgePanelWindow is not null)
        {
            _edgePanelWindow.AllowClose();
            _edgePanelWindow.Close();
            _edgePanelWindow = null;
        }
        try
        {
            if (_host is not null)
            {
                var server = _host.Services.GetRequiredService<IDeviceServer>();
                await server.StopAsync();
                await _host.StopAsync();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"DeviceSync shutdown error: {exception}");
        }
        finally
        {
            _host?.Dispose();
            _host = null;

            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
                _ownsSingleInstanceMutex = false;
            }

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
