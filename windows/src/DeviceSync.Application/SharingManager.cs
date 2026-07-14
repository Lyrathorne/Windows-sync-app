using System.Text.Json;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public interface IFeatureMessageTransport
{
    bool IsConnected { get; }
    string? LocalDeviceId { get; }
    event EventHandler<FeatureMessageEventArgs>? MessageReceived;
    Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default);
}

public sealed class FeatureMessageEventArgs(string type, JsonElement payload) : EventArgs
{
    public string Type { get; } = type;
    public JsonElement Payload { get; } = payload;
}

public sealed record SharedTextItem(string ItemId, string Kind, string Text, DateTimeOffset ReceivedAtUtc);

public sealed class SharingManager
{
    public const int MaximumTextBytes = 256 * 1024;
    private readonly IFeatureMessageTransport _transport;
    private readonly HashSet<string> _seenRevisions = new(StringComparer.Ordinal);
    private readonly List<SharedTextItem> _history = [];
    private readonly object _gate = new();
    private string? _lastAppliedClipboardText;
    private string? _lastObservedClipboardText;

    public SharingManager(IFeatureMessageTransport transport)
    {
        _transport = transport;
        _transport.MessageReceived += OnMessageReceived;
    }

    public bool ClipboardEnabled { get; set; }
    public IReadOnlyList<SharedTextItem> History { get { lock (_gate) return _history.ToArray(); } }
    public event Action<SharedTextItem>? ItemReceived;
    public event Action<string>? ClipboardReceived;

    public Task SendClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        Validate(text);
        if (!ClipboardEnabled) throw new InvalidOperationException("Clipboard synchronization is disabled.");
        var revision = Guid.NewGuid().ToString();
        lock (_gate) _seenRevisions.Add(revision);
        return _transport.SendAsync(ProtocolMessageTypes.ClipboardUpdate, ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
        {
            RevisionId = revision,
            SourceDeviceId = _transport.LocalDeviceId ?? throw new InvalidOperationException("No device is connected."),
            ContentType = Uri.TryCreate(text, UriKind.Absolute, out _) ? "text/uri-list" : "text/plain",
            Text = text,
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        }), cancellationToken);
    }

    public bool ShouldSendLocalClipboard(string text)
    {
        lock (_gate)
        {
            if (_lastAppliedClipboardText == text)
            {
                _lastAppliedClipboardText = null;
                _lastObservedClipboardText = text;
                return false;
            }
            if (_lastObservedClipboardText == text) return false;
            _lastObservedClipboardText = text;
            return true;
        }
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Validate(text);
        return _transport.SendAsync(ProtocolMessageTypes.TextShare, ProtocolSerializer.PayloadToJson(new TextSharePayload
        {
            ItemId = Guid.NewGuid().ToString(),
            Kind = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? "url" : "text",
            Text = text,
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        }), cancellationToken);
    }

    private void OnMessageReceived(object? sender, FeatureMessageEventArgs args)
    {
        if (args.Type == ProtocolMessageTypes.ClipboardUpdate)
        {
            var payload = ProtocolSerializer.DecodePayload<ClipboardUpdatePayload>(args.Payload);
            Validate(payload.Text);
            lock (_gate) if (!_seenRevisions.Add(payload.RevisionId)) return;
            if (ClipboardEnabled)
            {
                lock (_gate) _lastAppliedClipboardText = payload.Text;
                ClipboardReceived?.Invoke(payload.Text);
            }
            return;
        }
        if (args.Type == ProtocolMessageTypes.TextShare)
        {
            var payload = ProtocolSerializer.DecodePayload<TextSharePayload>(args.Payload);
            Validate(payload.Text);
            var item = new SharedTextItem(payload.ItemId, payload.Kind, payload.Text, DateTimeOffset.UtcNow);
            lock (_gate)
            {
                if (_history.Any(existing => existing.ItemId == item.ItemId)) return;
                _history.Insert(0, item);
                if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
            }
            ItemReceived?.Invoke(item);
        }
    }

    private static void Validate(string text)
    {
        if (string.IsNullOrEmpty(text) || System.Text.Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
            throw new InvalidDataException("Shared text is empty or exceeds 256 KiB.");
    }
}
