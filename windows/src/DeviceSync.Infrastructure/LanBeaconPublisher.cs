using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeviceSync.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceSync.Infrastructure;

public sealed class LanBeaconPublisher(
    ILogger<LanBeaconPublisher>? logger = null) : ILanBeaconPublisher, IDisposable
{
    public const int BeaconPort = 54322;
    private readonly ILogger<LanBeaconPublisher> _logger = logger ?? NullLogger<LanBeaconPublisher>.Instance;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(PublishedService service, CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = BroadcastLoopAsync(service, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task BroadcastLoopAsync(PublishedService service, CancellationToken cancellationToken)
    {
        var localAddresses = service.TxtRecords.TryGetValue("addresses", out var serializedAddresses)
            ? serializedAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
                .Where(address => address?.AddressFamily == AddressFamily.InterNetwork)
                .Cast<IPAddress>()
                .Distinct()
                .ToArray()
            : [];
        if (localAddresses.Length == 0 && IPAddress.TryParse(service.AdvertisedAddress, out var fallback))
            localAddresses = [fallback];
        var clients = localAddresses
            .Select(address => new UdpClient(new IPEndPoint(address, 0)) { EnableBroadcast = true })
            .ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            marker = "DeviceSyncLanBeaconV1",
            service.InstanceName,
            service.Port,
            service.AdvertisedAddress,
            service.TxtRecords,
        });
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var udp in clients)
                    {
                        await udp.SendAsync(
                            payload,
                            new IPEndPoint(IPAddress.Broadcast, BeaconPort),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is SocketException or IOException)
                {
                    _logger.LogDebug(error, "LAN beacon broadcast failed");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var udp in clients) udp.Dispose();
        }
    }

    public void Dispose() => _ = StopAsync();
}
