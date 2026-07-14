using System.Windows;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeviceSync.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _exitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddConsole();
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
                services.AddSingleton<DeviceSessionRegistry>();
                services.AddSingleton<IIncomingFileStorage, WindowsIncomingFileStorage>();
                services.AddSingleton<IIncomingTransferCheckpointStore, JsonIncomingTransferCheckpointStore>();
                services.AddSingleton<IncomingFileTransferDecisionCoordinator>();
                services.AddSingleton<IIncomingFileTransferDecisionService>(sp =>
                    sp.GetRequiredService<IncomingFileTransferDecisionCoordinator>());
                services.AddSingleton<IncomingFileTransferManager>();
                services.AddSingleton<WindowsFileTransferTransport>();
                services.AddSingleton<IOutgoingFileTransferTransport>(sp => sp.GetRequiredService<WindowsFileTransferTransport>());
                services.AddSingleton<OutgoingFileTransferManager>();
                services.AddSingleton<IOutgoingTransferQueueStore, JsonOutgoingTransferQueueStore>();
                services.AddSingleton<OutgoingTransferQueue>();
                services.AddSingleton<FeatureMessageTransport>();
                services.AddSingleton<IFeatureMessageTransport>(sp => sp.GetRequiredService<FeatureMessageTransport>());
                services.AddSingleton<SharingManager>();
                services.AddSingleton<NotificationManager>();
                services.AddSingleton<IFolderManifestBuilder, WindowsFolderManifestBuilder>();
                services.AddSingleton<IFolderSyncRootStore, JsonFolderSyncRootStore>();
                services.AddSingleton<FolderSyncManager>();
                services.AddSingleton<IFolderFileTransferAuthorizer>(sp => sp.GetRequiredService<FolderSyncManager>());
                services.AddSingleton<TcpDeviceServer>();
                services.AddSingleton<IServiceDiscoveryPublisher, SimpleMdnsServiceDiscoveryPublisher>();
                services.AddSingleton<DeviceServerManager>();
                services.AddSingleton<IDeviceServer>(sp => sp.GetRequiredService<DeviceServerManager>());
                services.AddSingleton<IDiscoveryControl>(sp => sp.GetRequiredService<DeviceServerManager>());
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<WindowsStartupService>();
                services.AddSingleton<IncomingFileViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
await _host.Services.GetRequiredService<IncomingFileTransferManager>().CleanupStalePartialsAsync();
await _host.Services.GetRequiredService<FolderSyncManager>().InitializeAsync();
await _host.Services.GetRequiredService<OutgoingTransferQueue>().StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Closing += (_, args) =>
        {
            if (_exitRequested) return;
            args.Cancel = true;
            window.Hide();
        };
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DeviceSync",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
        };
        _trayIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(() => { window.Show(); window.Activate(); }));
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => { _exitRequested = true; Shutdown(); }));
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => { window.Show(); window.Activate(); });
        _host.Services.GetRequiredService<NotificationManager>().Posted += notification => Dispatcher.Invoke(() =>
            _trayIcon?.ShowBalloonTip(
                5000,
                $"{notification.AppName}: {notification.Title}",
                notification.Text,
                System.Windows.Forms.ToolTipIcon.Info));
        if (!Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase)) window.Show();

        var server = _host.Services.GetRequiredService<IDeviceServer>();
        await server.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _exitRequested = true;
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        if (_host is not null)
        {
            var server = _host.Services.GetRequiredService<IDeviceServer>();
            await server.StopAsync();
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
