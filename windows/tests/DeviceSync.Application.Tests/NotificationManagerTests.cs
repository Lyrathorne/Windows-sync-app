using System.Text.Json;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class NotificationManagerTests
{
    [Fact]
    public void PostedUpdatedRemoved_AreRevisionedAndDoNotDuplicate()
    {
        var transport = new FakeTransport();
        var manager = new NotificationManager(transport);

        transport.Route(ProtocolMessageTypes.NotificationPosted, Posted(1, "one"));
        transport.Route(ProtocolMessageTypes.NotificationUpdated, Updated(2, "two"));
        transport.Route(ProtocolMessageTypes.NotificationUpdated, Updated(1, "stale"));

        var current = Assert.Single(manager.History);
        Assert.Equal("two", current.Text);
        Assert.Equal(2, current.Revision);

        transport.Route(ProtocolMessageTypes.NotificationRemoved, ProtocolSerializer.PayloadToJson(
            new NotificationRemovedPayload
            {
                NotificationId = "id", PackageName = "org.example", Revision = 3,
            }));
        transport.Route(ProtocolMessageTypes.NotificationUpdated, Updated(2, "late"));

        Assert.Empty(manager.History);
    }

    [Fact]
    public void DisconnectClearsSessionHistory()
    {
        var transport = new FakeTransport();
        var manager = new NotificationManager(transport);
        transport.Route(ProtocolMessageTypes.NotificationPosted, Posted(1, "one"));

        transport.SetConnected(false);

        Assert.Empty(manager.History);
    }

    [Fact]
    public void QuietHoursAndPerAppPreferenceSuppressSystemNotification()
    {
        var transport = new FakeTransport();
        var preferences = new InMemoryNotificationPreferences
        {
            QuietHoursStart = new TimeOnly(22, 0),
            QuietHoursEnd = new TimeOnly(7, 0),
        };
        var manager = new NotificationManager(transport, preferences);
        transport.Route(ProtocolMessageTypes.NotificationPosted, Posted(1, "one"));
        var notification = Assert.Single(manager.History);

        Assert.True(manager.IsQuietHours(new DateTimeOffset(2026, 7, 16, 23, 0, 0, TimeSpan.Zero)));
        preferences.SetAppAllowed("org.example", false);
        Assert.False(manager.ShouldShowSystemNotification(notification, privacyShielded: false));
    }

    [Fact]
    public async Task ActionRequiresCapabilityConfirmationAndNonDestructiveToken()
    {
        var transport = new FakeTransport();
        var manager = new NotificationManager(transport);
        transport.Route(ProtocolMessageTypes.NotificationPosted, Posted(1, "one", withAction: true));
        var notification = Assert.Single(manager.History);
        var action = Assert.Single(notification.Actions);

        await manager.InvokeActionAsync(notification, action, confirmedByUser: true);

        Assert.Equal(ProtocolMessageTypes.NotificationActionInvoke, transport.LastSentType);
        var payload = ProtocolSerializer.DecodePayload<NotificationActionInvokePayload>(transport.LastSentPayload!.Value);
        Assert.True(payload.ConfirmedByUser);
        Assert.Equal(action.ActionId, payload.ActionId);
    }

    private static JsonElement Posted(long revision, string text, bool withAction = false) =>
        ProtocolSerializer.PayloadToJson(new NotificationPostedPayload
        {
            NotificationId = "id",
            PackageName = "org.example",
            AppName = "Example",
            Title = "Title",
            Text = text,
            PostedAtUtc = "2026-07-16T10:00:00Z",
            Revision = revision,
            Actions = withAction
                ? [new NotificationActionPayload { ActionId = "open", Title = "Open" }]
                : [],
        });

    private static JsonElement Updated(long revision, string text) =>
        ProtocolSerializer.PayloadToJson(new NotificationUpdatedPayload
        {
            NotificationId = "id",
            PackageName = "org.example",
            AppName = "Example",
            Title = "Title",
            Text = text,
            PostedAtUtc = "2026-07-16T10:00:00Z",
            UpdatedAtUtc = "2026-07-16T10:01:00Z",
            Revision = revision,
        });

    private sealed class FakeTransport : IFeatureMessageTransport
    {
        public bool IsConnected { get; private set; } = true;
        public string? LocalDeviceId => "windows";
        public string? RemoteDeviceId => "android";
        public IReadOnlyCollection<string> Capabilities => [SupportedCapabilities.NotificationsV1];
        public string? LastSentType { get; private set; }
        public JsonElement? LastSentPayload { get; private set; }
        public event EventHandler<FeatureMessageEventArgs>? MessageReceived;
        public event EventHandler? ConnectionChanged;

        public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
        {
            LastSentType = type;
            LastSentPayload = payload;
            return Task.CompletedTask;
        }

        public void Route(string type, JsonElement payload) => MessageReceived?.Invoke(this, new(type, payload));
        public void SetConnected(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
