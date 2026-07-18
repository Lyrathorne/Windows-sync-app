namespace DeviceSync.Application;

public enum DeviceTransportKind
{
    Lan,
    Hotspot,
    UsbTethering,
    BluetoothRfcomm,
}

public sealed record DeviceTransportEndpoint(
    DeviceTransportKind Kind,
    string Address,
    int Port = 0,
    string? InterfaceId = null,
    bool IsRemembered = false);

public sealed record DeviceTransportMetrics(
    DeviceTransportKind Kind,
    bool IsAvailable,
    TimeSpan? ConnectLatency = null,
    TimeSpan? RoundTripTime = null,
    long BytesSent = 0,
    long BytesReceived = 0,
    string? LastErrorCode = null,
    DateTimeOffset? MeasuredAtUtc = null);

public sealed record DeviceTransportProfile(
    DeviceTransportKind Kind,
    int Priority,
    int MaximumFrameBytes,
    long MaximumFileBytes,
    bool IsSlow,
    IReadOnlySet<string> DisabledCapabilities)
{
    public static DeviceTransportProfile For(DeviceTransportKind kind) => kind switch
    {
        DeviceTransportKind.BluetoothRfcomm => new(
            kind,
            Priority: 10,
            MaximumFrameBytes: 48 * 1024,
            MaximumFileBytes: 2 * 1024 * 1024,
            IsSlow: true,
            DisabledCapabilities: new HashSet<string>(StringComparer.Ordinal)
            {
                "media-catalog-v1",
                "thumbnails-v1",
                "folder-sync-v1",
                "file-transfer-v2",
            }),
        DeviceTransportKind.UsbTethering => new(kind, 100, 1024 * 1024, 100 * 1024 * 1024, false, Empty),
        DeviceTransportKind.Lan => new(kind, 90, 1024 * 1024, 100 * 1024 * 1024, false, Empty),
        DeviceTransportKind.Hotspot => new(kind, 80, 1024 * 1024, 100 * 1024 * 1024, false, Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static IReadOnlySet<string> Empty { get; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed class DeviceTransportSelector
{
    public DeviceTransportEndpoint? SelectBest(
        IEnumerable<DeviceTransportEndpoint> endpoints,
        IReadOnlyDictionary<DeviceTransportKind, DeviceTransportMetrics>? metrics = null)
    {
        return endpoints
            .Where(endpoint => metrics is null ||
                !metrics.TryGetValue(endpoint.Kind, out var value) ||
                value.IsAvailable)
            .OrderByDescending(endpoint => DeviceTransportProfile.For(endpoint.Kind).Priority)
            .ThenBy(endpoint => metrics is not null &&
                metrics.TryGetValue(endpoint.Kind, out var value)
                    ? value.ConnectLatency ?? TimeSpan.MaxValue
                    : TimeSpan.MaxValue)
            .ThenByDescending(endpoint => endpoint.IsRemembered)
            .FirstOrDefault();
    }
}

public sealed class RecentMessageDeduplicator(
    int capacity = 4096,
    TimeSpan? retention = null)
{
    private readonly int _capacity = Math.Max(64, capacity);
    private readonly TimeSpan _retention = retention ?? TimeSpan.FromHours(12);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset SeenAt)> _order = new();

    public bool TryAccept(string senderDeviceId, string messageId, DateTimeOffset now)
    {
        var key = $"{senderDeviceId}\n{messageId}";
        lock (_gate)
        {
            Trim(now);
            if (_seen.ContainsKey(key)) return false;
            _seen[key] = now;
            _order.Enqueue((key, now));
            Trim(now);
            return true;
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_order.TryPeek(out var item) &&
               (_seen.Count > _capacity || now - item.SeenAt > _retention))
        {
            _order.Dequeue();
            if (_seen.TryGetValue(item.Key, out var current) && current == item.SeenAt)
                _seen.Remove(item.Key);
        }
    }
}
