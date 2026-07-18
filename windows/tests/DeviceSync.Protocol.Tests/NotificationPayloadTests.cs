using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class NotificationPayloadTests
{
    [Fact]
    public void PostedVector_UsesCamelCaseAndIgnoresUnknownFields()
    {
        var message = Read("01-notification-posted.json");
        var payload = ProtocolSerializer.DecodePayload<NotificationPostedPayload>(message.Payload);

        Assert.Equal(ProtocolMessageTypes.NotificationPosted, message.Type);
        Assert.Equal("mOrzZP0Tei8zz-vector", payload.NotificationId);
        Assert.Equal("org.example.chat", payload.PackageName);
        Assert.Equal("msg", payload.Category);
        Assert.Equal(1, payload.Revision);
        Assert.Single(payload.Actions);
        Assert.True(payload.Actions[0].RequiresConfirmation);
        Assert.False(payload.Actions[0].IsDestructive);
    }

    [Fact]
    public void UpdatedRemovedAndActionVectors_RoundTrip()
    {
        var updated = ProtocolSerializer.DecodePayload<NotificationUpdatedPayload>(
            Read("02-notification-updated.json").Payload);
        var removed = ProtocolSerializer.DecodePayload<NotificationRemovedPayload>(
            Read("03-notification-removed.json").Payload);
        var action = ProtocolSerializer.DecodePayload<NotificationActionInvokePayload>(
            Read("04-notification-action-invoke.json").Payload);

        Assert.Equal(2, updated.Revision);
        Assert.Equal("See you at 19:00", updated.Text);
        Assert.Equal(3, removed.Revision);
        Assert.Equal("removed", removed.Reason);
        Assert.True(action.ConfirmedByUser);
    }

    private static ProtocolMessage Read(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "notifications", fileName);
        return ProtocolSerializer.Deserialize(File.ReadAllText(path));
    }
}
