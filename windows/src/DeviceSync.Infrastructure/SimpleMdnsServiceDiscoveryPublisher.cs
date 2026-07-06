using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DeviceSync.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceSync.Infrastructure;

public sealed class SimpleMdnsServiceDiscoveryPublisher : IServiceDiscoveryPublisher, IDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;
    private readonly ILogger<SimpleMdnsServiceDiscoveryPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private Task? _listenTask;

    public SimpleMdnsServiceDiscoveryPublisher(ILogger<SimpleMdnsServiceDiscoveryPublisher>? logger = null)
    {
        _logger = logger ?? NullLogger<SimpleMdnsServiceDiscoveryPublisher>.Instance;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public PublisherState State { get; private set; } = PublisherState.Stopped;
    public PublishedService? CurrentService { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastPublishedAtUtc { get; private set; }
    public event EventHandler<PublisherStateChangedEventArgs>? StateChanged;

    public async Task StartAsync(PublishedService service, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is PublisherState.Published && CurrentService == service)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            SetState(PublisherState.Starting, service, null);
            CurrentService = service;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
                MulticastLoopback = false,
            };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            _udp.JoinMulticastGroup(MulticastAddress);
            _listenTask = ListenAsync(_cts.Token);
            await AnnounceAsync(service, _cts.Token).ConfigureAwait(false);
            LastPublishedAtUtc = DateTimeOffset.UtcNow;
            SetState(PublisherState.Published, service, null);
            _logger.LogInformation("Service published");
        }
        catch (Exception error)
        {
            LastError = error.Message;
            SetState(PublisherState.Failed, service, error.Message);
            _logger.LogWarning(error, "Service publication failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(PublisherState.Stopping, CurrentService, null);
            await StopCoreAsync().ConfigureAwait(false);
            SetState(PublisherState.Stopped, null, null);
            _logger.LogInformation("Service publication stopped");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var service = CurrentService;
            if (service is null) continue;
            if (DnsMessage.IsQueryFor(result.Buffer, service.ServiceType))
            {
                await AnnounceAsync(service, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task AnnounceAsync(PublishedService service, CancellationToken cancellationToken)
    {
        var hostName = $"{Environment.MachineName}.local";
        var packet = DnsMessage.BuildResponse(service, hostName);
        await _udp!.SendAsync(packet, new IPEndPoint(MulticastAddress, MdnsPort), cancellationToken).ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        if (_listenTask is not null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
        _cts = null;
        _udp = null;
        _listenTask = null;
        CurrentService = null;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _ = RepublishAsync();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => _ = RepublishAsync();

    private async Task RepublishAsync()
    {
        var service = CurrentService;
        if (service is null || State != PublisherState.Published) return;
        _logger.LogInformation("Network changed");
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _logger.LogInformation("Republishing service");
        await StartAsync(service).ConfigureAwait(false);
    }

    private void SetState(PublisherState state, PublishedService? service, string? error)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(this, new PublisherStateChangedEventArgs(state, service, LastError, LastPublishedAtUtc));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _ = StopAsync();
    }

    private static class DnsMessage
    {
        public static bool IsQueryFor(byte[] packet, string serviceType)
        {
            var text = Encoding.UTF8.GetString(packet);
            return text.Contains(serviceType, StringComparison.OrdinalIgnoreCase)
                || text.Contains("_services._dns-sd._udp", StringComparison.OrdinalIgnoreCase);
        }

        public static byte[] BuildResponse(PublishedService service, string hostName)
        {
            var serviceType = $"{service.ServiceType}.local";
            var instance = $"{service.InstanceName}.{serviceType}";
            using var stream = new MemoryStream();
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0x8400);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 4);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WritePtr(stream, serviceType, instance);
            WriteSrv(stream, instance, service.Port, hostName);
            WriteTxt(stream, instance, service.TxtRecords);
            WriteA(stream, hostName);
            return stream.ToArray();
        }

        private static void WritePtr(Stream stream, string name, string target)
        {
            WriteName(stream, name);
            WriteUInt16(stream, 12);
            WriteUInt16(stream, 1);
            WriteUInt32(stream, 120);
            using var data = new MemoryStream();
            WriteName(data, target);
            WriteUInt16(stream, (ushort)data.Length);
            data.WriteTo(stream);
        }

        private static void WriteSrv(Stream stream, string name, int port, string target)
        {
            WriteName(stream, name);
            WriteUInt16(stream, 33);
            WriteUInt16(stream, 1);
            WriteUInt32(stream, 120);
            using var data = new MemoryStream();
            WriteUInt16(data, 0);
            WriteUInt16(data, 0);
            WriteUInt16(data, (ushort)port);
            WriteName(data, target);
            WriteUInt16(stream, (ushort)data.Length);
            data.WriteTo(stream);
        }

        private static void WriteTxt(Stream stream, string name, IReadOnlyDictionary<string, string> records)
        {
            WriteName(stream, name);
            WriteUInt16(stream, 16);
            WriteUInt16(stream, 1);
            WriteUInt32(stream, 120);
            using var data = new MemoryStream();
            foreach (var (key, value) in records)
            {
                var bytes = Encoding.UTF8.GetBytes($"{key}={value}");
                if (bytes.Length > 255) continue;
                data.WriteByte((byte)bytes.Length);
                data.Write(bytes);
            }
            WriteUInt16(stream, (ushort)data.Length);
            data.WriteTo(stream);
        }

        private static void WriteA(Stream stream, string hostName)
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (address is null) address = IPAddress.Loopback;
            WriteName(stream, hostName);
            WriteUInt16(stream, 1);
            WriteUInt16(stream, 1);
            WriteUInt32(stream, 120);
            var bytes = address.GetAddressBytes();
            WriteUInt16(stream, (ushort)bytes.Length);
            stream.Write(bytes);
        }

        private static void WriteName(Stream stream, string name)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes);
            }
            stream.WriteByte(0);
        }

        private static void WriteUInt16(Stream stream, int value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
            stream.Write(buffer);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            stream.Write(buffer);
        }
    }
}
