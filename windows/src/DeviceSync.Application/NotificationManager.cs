using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed record ReceivedNotification(
    string NotificationId,
    string PackageName,
    string AppName,
    string Title,
    string Text,
    DateTimeOffset PostedAtUtc);

public sealed class NotificationManager
{
    private readonly List<ReceivedNotification> _history = [];
    private readonly object _gate = new();
    public NotificationManager(IFeatureMessageTransport transport) => transport.MessageReceived += OnMessageReceived;
    public IReadOnlyList<ReceivedNotification> History { get { lock (_gate) return _history.ToArray(); } }
    public event Action<ReceivedNotification>? Posted;
    public event Action<string>? Removed;

    private void OnMessageReceived(object? sender, FeatureMessageEventArgs args)
    {
        if (args.Type == ProtocolMessageTypes.NotificationPosted)
        {
            var payload = ProtocolSerializer.DecodePayload<NotificationPostedPayload>(args.Payload);
            if (payload.Title.Length > 4096 || payload.Text.Length > 8192) return;
            var notification = new ReceivedNotification(
                payload.NotificationId, payload.PackageName, payload.AppName,
                payload.Title, payload.Text,
                DateTimeOffset.TryParse(payload.PostedAtUtc, out var posted) ? posted : DateTimeOffset.UtcNow);
            lock (_gate)
            {
                _history.RemoveAll(item => item.NotificationId == notification.NotificationId);
                _history.Insert(0, notification);
                if (_history.Count > 100) _history.RemoveRange(100, _history.Count - 100);
            }
            Posted?.Invoke(notification);
        }
        else if (args.Type == ProtocolMessageTypes.NotificationRemoved)
        {
            var payload = ProtocolSerializer.DecodePayload<NotificationRemovedPayload>(args.Payload);
            lock (_gate) _history.RemoveAll(item => item.NotificationId == payload.NotificationId);
            Removed?.Invoke(payload.NotificationId);
        }
    }
}
