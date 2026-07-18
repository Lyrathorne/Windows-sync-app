using System.Text;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed record ReceivedNotificationAction(
    string ActionId,
    string Title,
    string Semantic,
    bool RequiresConfirmation,
    bool IsDestructive);

public sealed record ReceivedNotification(
    string NotificationId,
    string PackageName,
    string AppName,
    string Title,
    string Text,
    DateTimeOffset PostedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Category,
    string? GroupKey,
    bool IsSilent,
    bool IsSensitive,
    string? IconToken,
    long Revision,
    IReadOnlyList<ReceivedNotificationAction> Actions)
{
    public string Key => $"{PackageName}\u001f{NotificationId}";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? AppName : Title;
}

public interface INotificationPreferences
{
    bool ToastsEnabled { get; set; }
    bool HideSensitiveWhenLocked { get; set; }
    TimeOnly? QuietHoursStart { get; set; }
    TimeOnly? QuietHoursEnd { get; set; }
    IReadOnlySet<string> BlockedPackages { get; }
    bool IsAppAllowed(string packageName);
    void SetAppAllowed(string packageName, bool allowed);
}

public sealed class InMemoryNotificationPreferences : INotificationPreferences
{
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    public bool ToastsEnabled { get; set; } = true;
    public bool HideSensitiveWhenLocked { get; set; } = true;
    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }
    public IReadOnlySet<string> BlockedPackages => _blocked;
    public bool IsAppAllowed(string packageName) => !_blocked.Contains(packageName);
    public void SetAppAllowed(string packageName, bool allowed)
    {
        if (allowed) _blocked.Remove(packageName);
        else _blocked.Add(packageName);
    }
}

public sealed class NotificationManager
{
    public const int MaximumHistory = 200;
    private const int MaximumTitleCharacters = 256;
    private const int MaximumTextBytes = 8 * 1024;
    private readonly IFeatureMessageTransport _transport;
    private readonly INotificationPreferences _preferences;
    private readonly List<ReceivedNotification> _history = [];
    private readonly Dictionary<string, long> _latestRevisions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public NotificationManager(
        IFeatureMessageTransport transport,
        INotificationPreferences? preferences = null)
    {
        _transport = transport;
        _preferences = preferences ?? new InMemoryNotificationPreferences();
        transport.MessageReceived += OnMessageReceived;
        transport.ConnectionChanged += OnConnectionChanged;
    }

    public IReadOnlyList<ReceivedNotification> History
    {
        get { lock (_gate) return _history.ToArray(); }
    }

    public bool IsConnected => _transport.IsConnected;
    public INotificationPreferences Preferences => _preferences;
    public event Action<ReceivedNotification>? Posted;
    public event Action<ReceivedNotification>? Updated;
    public event Action<string>? Removed;
    public event Action? Cleared;
    public event Action<string>? ActionResultReceived;
    public event EventHandler? ConnectionChanged;

    public bool IsQuietHours(DateTimeOffset? now = null)
    {
        var start = _preferences.QuietHoursStart;
        var end = _preferences.QuietHoursEnd;
        if (start is null || end is null || start == end) return false;
        var local = TimeOnly.FromDateTime((now ?? DateTimeOffset.Now).LocalDateTime);
        return start < end ? local >= start && local < end : local >= start || local < end;
    }

    public bool ShouldShowSystemNotification(ReceivedNotification notification, bool privacyShielded)
        => _preferences.ToastsEnabled
            && _preferences.IsAppAllowed(notification.PackageName)
            && !notification.IsSilent
            && !IsQuietHours()
            && !(privacyShielded && notification.IsSensitive && _preferences.HideSensitiveWhenLocked);

    public void SetAppAllowed(string packageName, bool allowed) =>
        _preferences.SetAppAllowed(packageName, allowed);

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            _latestRevisions.Clear();
        }
        Cleared?.Invoke();
    }

    public async Task InvokeActionAsync(
        ReceivedNotification notification,
        ReceivedNotificationAction action,
        bool confirmedByUser,
        CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected) throw new InvalidOperationException("The Android device is offline.");
        if (!_transport.Capabilities.Contains(SupportedCapabilities.NotificationsV1))
            throw new InvalidOperationException("The connected device does not support notification actions.");
        if (!confirmedByUser || action.IsDestructive)
            throw new InvalidOperationException("This notification action requires explicit confirmation and must be non-destructive.");
        if (!notification.Actions.Any(candidate => candidate.ActionId == action.ActionId))
            throw new InvalidOperationException("The notification action is no longer available.");

        await _transport.SendAsync(
            ProtocolMessageTypes.NotificationActionInvoke,
            ProtocolSerializer.PayloadToJson(new NotificationActionInvokePayload
            {
                InvocationId = Guid.NewGuid().ToString(),
                NotificationId = notification.NotificationId,
                PackageName = notification.PackageName,
                ActionId = action.ActionId,
                ConfirmedByUser = true,
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private void OnConnectionChanged(object? sender, EventArgs args)
    {
        if (!_transport.IsConnected) ClearHistory();
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMessageReceived(object? sender, FeatureMessageEventArgs args)
    {
        switch (args.Type)
        {
            case ProtocolMessageTypes.NotificationPosted:
                HandlePosted(ProtocolSerializer.DecodePayload<NotificationPostedPayload>(args.Payload));
                break;
            case ProtocolMessageTypes.NotificationUpdated:
                HandleUpdated(ProtocolSerializer.DecodePayload<NotificationUpdatedPayload>(args.Payload));
                break;
            case ProtocolMessageTypes.NotificationRemoved:
                HandleRemoved(ProtocolSerializer.DecodePayload<NotificationRemovedPayload>(args.Payload));
                break;
            case ProtocolMessageTypes.NotificationActionResult:
                var result = ProtocolSerializer.DecodePayload<NotificationActionResultPayload>(args.Payload);
                ActionResultReceived?.Invoke(result.Status);
                break;
        }
    }

    private void HandlePosted(NotificationPostedPayload payload)
    {
        var notification = Map(
            payload.NotificationId, payload.PackageName, payload.AppName, payload.Title, payload.Text,
            payload.PostedAtUtc, payload.PostedAtUtc, payload.Category, payload.GroupKey, payload.IsSilent,
            payload.IsSensitive, payload.IconToken, payload.Revision, payload.Actions);
        Apply(notification, isUpdateMessage: false);
    }

    private void HandleUpdated(NotificationUpdatedPayload payload)
    {
        var notification = Map(
            payload.NotificationId, payload.PackageName, payload.AppName, payload.Title, payload.Text,
            payload.PostedAtUtc, payload.UpdatedAtUtc, payload.Category, payload.GroupKey, payload.IsSilent,
            payload.IsSensitive, payload.IconToken, payload.Revision, payload.Actions);
        Apply(notification, isUpdateMessage: true);
    }

    private void Apply(ReceivedNotification notification, bool isUpdateMessage)
    {
        var changedExisting = false;
        lock (_gate)
        {
            if (_latestRevisions.TryGetValue(notification.Key, out var latest) && notification.Revision <= latest) return;
            _latestRevisions[notification.Key] = notification.Revision;
            changedExisting = _history.RemoveAll(item => item.Key == notification.Key) > 0;
            _history.Insert(0, notification);
            if (_history.Count > MaximumHistory)
                _history.RemoveRange(MaximumHistory, _history.Count - MaximumHistory);
        }
        if (changedExisting || isUpdateMessage) Updated?.Invoke(notification);
        else Posted?.Invoke(notification);
    }

    private void HandleRemoved(NotificationRemovedPayload payload)
    {
        ValidateIdentity(payload.NotificationId, payload.PackageName);
        var key = $"{payload.PackageName}\u001f{payload.NotificationId}";
        lock (_gate)
        {
            if (_latestRevisions.TryGetValue(key, out var latest) && payload.Revision > 0 && payload.Revision <= latest) return;
            _latestRevisions[key] = Math.Max(payload.Revision, latest + 1);
            _history.RemoveAll(item => item.Key == key);
        }
        Removed?.Invoke(key);
    }

    private static ReceivedNotification Map(
        string notificationId,
        string packageName,
        string appName,
        string title,
        string text,
        string postedAtUtc,
        string updatedAtUtc,
        string category,
        string? groupKey,
        bool isSilent,
        bool isSensitive,
        string? iconToken,
        long revision,
        IReadOnlyList<NotificationActionPayload> actions)
    {
        ValidateIdentity(notificationId, packageName);
        if (appName.Length > 256 || title.Length > MaximumTitleCharacters ||
            Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
            throw new InvalidDataException("Notification payload exceeds Notifications V1 limits.");
        if (actions.Count > 3 || actions.Any(action =>
                string.IsNullOrWhiteSpace(action.ActionId) || action.ActionId.Length > 256 ||
                string.IsNullOrWhiteSpace(action.Title) || action.Title.Length > 128))
            throw new InvalidDataException("Notification actions exceed Notifications V1 limits.");
        var effectiveRevision = Math.Max(1, revision);
        return new ReceivedNotification(
            notificationId,
            packageName,
            appName,
            title,
            text,
            DateTimeOffset.TryParse(postedAtUtc, out var posted) ? posted : DateTimeOffset.UtcNow,
            DateTimeOffset.TryParse(updatedAtUtc, out var updated) ? updated : DateTimeOffset.UtcNow,
            category,
            groupKey,
            isSilent,
            isSensitive,
            iconToken,
            effectiveRevision,
            actions.Where(action => !action.IsDestructive).Select(action => new ReceivedNotificationAction(
                action.ActionId,
                action.Title,
                action.Semantic,
                action.RequiresConfirmation,
                action.IsDestructive)).ToArray());
    }

    private static void ValidateIdentity(string notificationId, string packageName)
    {
        if (string.IsNullOrWhiteSpace(notificationId) || notificationId.Length > 256 ||
            string.IsNullOrWhiteSpace(packageName) || packageName.Length > 256)
            throw new InvalidDataException("Notification identity is invalid.");
    }
}
