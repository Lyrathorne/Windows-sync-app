using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public interface IFeatureMessageTransport
{
    bool IsConnected { get; }
    string? LocalDeviceId { get; }
    string? RemoteDeviceId { get; }
    IReadOnlyCollection<string> Capabilities => [];
    event EventHandler<FeatureMessageEventArgs>? MessageReceived;
    event EventHandler? ConnectionChanged { add { } remove { } }
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
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RevisionLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumSeenRevisions = 512;

    private sealed record PendingClipboard(string Text, DateTimeOffset CreatedAtUtc);

    private readonly IFeatureMessageTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, DateTimeOffset> _seenRevisions = new(StringComparer.Ordinal);
    private readonly List<SharedTextItem> _history = [];
    private readonly object _gate = new();
    private string? _lastAppliedClipboardText;
    private string? _lastObservedClipboardText;
    private string? _lastRemoteDeviceId;
    private PendingClipboard? _pendingClipboard;

    public SharingManager(IFeatureMessageTransport transport, TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transport.MessageReceived += OnMessageReceived;
    }

    public bool ClipboardEnabled { get; set; }
    public Func<string, bool> IsDeviceAllowed { get; set; } = _ => false;
    public bool IsConnected => _transport.IsConnected;
    public string? CurrentRemoteDeviceId => _transport.RemoteDeviceId ?? _lastRemoteDeviceId;
    public bool IsCurrentDeviceAllowed => CurrentRemoteDeviceId is { } id && IsDeviceAllowed(id);
    public IReadOnlyList<SharedTextItem> History { get { lock (_gate) return _history.ToArray(); } }
    public event Action<SharedTextItem>? ItemReceived;
    public event Action<string>? ClipboardReceived;

    public void ClearHistory()
    {
        lock (_gate) _history.Clear();
    }

    public async Task SendClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        RequireAutomaticPermission();
        Validate(text);
        if (!_transport.IsConnected)
        {
            lock (_gate) _pendingClipboard = new(text, Now());
            Trace.WriteLine($"CLIPBOARD_UPDATE_QUEUED sizeBytes={Encoding.UTF8.GetByteCount(text)}");
            return;
        }
        await SendClipboardInternalAsync(text, isManual: false, cancellationToken).ConfigureAwait(false);
    }

    public Task SendClipboardNowAsync(string text, CancellationToken cancellationToken = default) =>
        SendClipboardInternalAsync(text, isManual: true, cancellationToken);

    public async Task FlushPendingClipboardAsync(CancellationToken cancellationToken = default)
    {
        if (!ClipboardEnabled || !IsCurrentDeviceAllowed || !_transport.IsConnected) return;
        PendingClipboard? pending;
        lock (_gate)
        {
            pending = _pendingClipboard;
            if (pending is not null && Now() - pending.CreatedAtUtc > PendingLifetime)
            {
                _pendingClipboard = null;
                pending = null;
            }
        }
        if (pending is null) return;
        await SendClipboardInternalAsync(pending.Text, isManual: false, cancellationToken).ConfigureAwait(false);
        lock (_gate) if (_pendingClipboard == pending) _pendingClipboard = null;
    }

    private async Task SendClipboardInternalAsync(string text, bool isManual, CancellationToken cancellationToken)
    {
        Validate(text);
        var remote = _transport.RemoteDeviceId ?? throw new InvalidOperationException("No authenticated device is connected.");
        _lastRemoteDeviceId = remote;
        var revision = Guid.NewGuid().ToString();
        RememberRevision(revision);
        await _transport.SendAsync(ProtocolMessageTypes.ClipboardUpdate, ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
        {
            RevisionId = revision,
            OriginDeviceId = _transport.LocalDeviceId ?? throw new InvalidOperationException("No local device identity is available."),
            ContentType = IsSafeHttpUrl(text) ? "text/uri-list" : "text/plain",
            Text = text,
            CreatedAtUtc = Now().ToString("O"),
            IsManual = isManual,
        }), cancellationToken).ConfigureAwait(false);
        lock (_gate) _lastObservedClipboardText = text;
        AddToHistory(new SharedTextItem(revision, IsSafeHttpUrl(text) ? "url" : "clipboard", text, Now()));
        Trace.WriteLine($"CLIPBOARD_UPDATE_SENT revisionId={revision} sizeBytes={Encoding.UTF8.GetByteCount(text)} manual={isManual}");
    }

    public bool ShouldSendLocalClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        lock (_gate)
        {
            if (_lastAppliedClipboardText == text)
            {
                _lastAppliedClipboardText = null;
                _lastObservedClipboardText = text;
                return false;
            }
            if (_lastObservedClipboardText == text || _pendingClipboard?.Text == text) return false;
            _lastObservedClipboardText = text;
            return true;
        }
    }

    public void MarkLocalClipboardSendFailed(string text)
    {
        lock (_gate)
        {
            if (_lastObservedClipboardText == text) _lastObservedClipboardText = null;
            if (ClipboardEnabled && CurrentRemoteDeviceId is { } id && IsDeviceAllowed(id))
                _pendingClipboard = new(text, Now());
        }
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Validate(text);
        var item = new SharedTextItem(Guid.NewGuid().ToString(), IsSafeHttpUrl(text) ? "url" : "text", text, Now());
        await _transport.SendAsync(ProtocolMessageTypes.TextShare, ProtocolSerializer.PayloadToJson(new TextSharePayload
        {
            ItemId = item.ItemId,
            Kind = item.Kind,
            Text = text,
            CreatedAtUtc = Now().ToString("O"),
        }), cancellationToken).ConfigureAwait(false);
        AddToHistory(item);
    }

    private void OnMessageReceived(object? sender, FeatureMessageEventArgs args)
    {
        if (args.Type == ProtocolMessageTypes.ClipboardUpdate)
        {
            HandleClipboardUpdate(ProtocolSerializer.DecodePayload<ClipboardUpdatePayload>(args.Payload));
            return;
        }
        if (args.Type != ProtocolMessageTypes.TextShare) return;
        var payload = ProtocolSerializer.DecodePayload<TextSharePayload>(args.Payload);
        Validate(payload.Text);
        var item = new SharedTextItem(payload.ItemId, payload.Kind, payload.Text, Now());
        AddToHistory(item);
    }

    private void HandleClipboardUpdate(ClipboardUpdatePayload payload)
    {
        Validate(payload.Text);
        if (string.IsNullOrWhiteSpace(payload.RevisionId) || string.IsNullOrWhiteSpace(payload.OriginDeviceId))
            throw new InvalidDataException("Clipboard identity metadata is missing.");
        var authenticatedRemote = _transport.RemoteDeviceId ?? throw new InvalidDataException("No authenticated clipboard sender.");
        if (!string.Equals(authenticatedRemote, payload.OriginDeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("Clipboard origin does not match the authenticated sender.");
        if (payload.ContentType is not ("text/plain" or "text/uri-list"))
            throw new InvalidDataException("Clipboard content type is not supported.");
        if (payload.ContentType == "text/uri-list" && !IsSafeHttpUrl(payload.Text))
            throw new InvalidDataException("Clipboard URL must use HTTP or HTTPS.");
        if (!RememberRevision(payload.RevisionId)) return;
        _lastRemoteDeviceId = authenticatedRemote;
        if (!payload.IsManual && (!ClipboardEnabled || !IsDeviceAllowed(authenticatedRemote))) return;
        lock (_gate)
        {
            if (_lastAppliedClipboardText == payload.Text) return;
            _lastAppliedClipboardText = payload.Text;
        }
        AddToHistory(new SharedTextItem(
            payload.RevisionId,
            payload.ContentType == "text/uri-list" ? "url" : "clipboard",
            payload.Text,
            Now()));
        ClipboardReceived?.Invoke(payload.Text);
        Trace.WriteLine($"CLIPBOARD_UPDATE_APPLIED revisionId={payload.RevisionId} sizeBytes={Encoding.UTF8.GetByteCount(payload.Text)}");
    }

    private bool RememberRevision(string revisionId)
    {
        lock (_gate)
        {
            var cutoff = Now() - RevisionLifetime;
            foreach (var expired in _seenRevisions.Where(entry => entry.Value < cutoff).Select(entry => entry.Key).ToArray())
                _seenRevisions.Remove(expired);
            if (!_seenRevisions.TryAdd(revisionId, Now())) return false;
            while (_seenRevisions.Count > MaximumSeenRevisions) _seenRevisions.Remove(_seenRevisions.First().Key);
            return true;
        }
    }

    private void AddToHistory(SharedTextItem item)
    {
        lock (_gate)
        {
            if (_history.Any(existing => existing.ItemId == item.ItemId)) return;
            _history.RemoveAll(existing => string.Equals(existing.Text, item.Text, StringComparison.Ordinal));
            _history.Insert(0, item);
            if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
        }
        ItemReceived?.Invoke(item);
    }

    private void RequireAutomaticPermission()
    {
        if (!ClipboardEnabled) throw new InvalidOperationException("Clipboard synchronization is disabled.");
        if (!IsCurrentDeviceAllowed) throw new InvalidOperationException("Clipboard synchronization is not allowed for this device.");
    }

    private static void Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Shared text is empty.");
        if (Encoding.UTF8.GetByteCount(text) > MaximumTextBytes) throw new InvalidDataException("Shared text exceeds 256 KiB.");
    }

    private static bool IsSafeHttpUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();
}
