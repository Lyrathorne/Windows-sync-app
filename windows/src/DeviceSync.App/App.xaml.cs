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
                services.AddSingleton<IDeviceIdentityKeyProvider, WindowsDeviceIdentityKeyProvider>();
                services.AddSingleton<IPairingSessionManager, PairingSessionManager>();
                services.AddSingleton<ITrustedDeviceRepository, FileTrustedDeviceRepository>();
                services.AddSingleton<IQrCodeGenerator, QrCodeGenerator>();
                services.AddSingleton<ILocalNetworkAddressProvider, LocalNetworkAddressProvider>();
                services.AddSingleton<DeviceSessionRegistry>();
                services.AddSingleton<TcpDeviceServer>();
                services.AddSingleton<IServiceDiscoveryPublisher, SimpleMdnsServiceDiscoveryPublisher>();
                services.AddSingleton<DeviceServerManager>();
                services.AddSingleton<IDeviceServer>(sp => sp.GetRequiredService<DeviceServerManager>());
                services.AddSingleton<IDiscoveryControl>(sp => sp.GetRequiredService<DeviceServerManager>());
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();

        var server = _host.Services.GetRequiredService<IDeviceServer>();
        await server.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
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
