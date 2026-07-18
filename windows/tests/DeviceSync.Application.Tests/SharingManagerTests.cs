using System.Text.Json;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class SharingManagerTests
{
    [Fact]
    public void ClipboardRevision_IsAppliedOnlyOnce()
    {
        var transport = new FakeFeatureTransport();
        var manager = new SharingManager(transport) { ClipboardEnabled = true, IsDeviceAllowed = _ => true };
        var received = new List<string>();
        manager.ClipboardReceived += received.Add;
        var payload = ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
        {
            RevisionId = "revision-1", OriginDeviceId = "android", ContentType = "text/plain",
            Text = "hello", CreatedAtUtc = "2026-07-14T00:00:00Z",
        });

        transport.Route(ProtocolMessageTypes.ClipboardUpdate, payload);
        transport.Route(ProtocolMessageTypes.ClipboardUpdate, payload);

        Assert.Equal(["hello"], received);
        Assert.Single(manager.History);
        Assert.Equal("clipboard", manager.History[0].Kind);
        Assert.False(manager.ShouldSendLocalClipboard("hello"));
    }

    [Fact]
    public void TextHistory_IsIdempotent()
    {
        var transport = new FakeFeatureTransport();
        var manager = new SharingManager(transport);
        var payload = ProtocolSerializer.PayloadToJson(new TextSharePayload
        {
            ItemId = "item-1", Kind = "url", Text = "https://example.com", CreatedAtUtc = "2026-07-14T00:00:00Z",
        });
        transport.Route(ProtocolMessageTypes.TextShare, payload);
        transport.Route(ProtocolMessageTypes.TextShare, payload);
        Assert.Single(manager.History);
    }

    [Fact]
    public async Task FailedClipboardSend_IsCoalescedAndFlushedAfterReconnect()
    {
        var transport = new FakeFeatureTransport { IsConnected = false };
        var manager = new SharingManager(transport) { ClipboardEnabled = true, IsDeviceAllowed = _ => true };

        Assert.True(manager.ShouldSendLocalClipboard("retry me"));
        Assert.False(manager.ShouldSendLocalClipboard("retry me"));

        manager.MarkLocalClipboardSendFailed("retry me");

        Assert.False(manager.ShouldSendLocalClipboard("retry me"));
        transport.IsConnected = true;
        await manager.FlushPendingClipboardAsync();
        Assert.Equal(ProtocolMessageTypes.ClipboardUpdate, transport.LastSentType);
    }

    [Fact]
    public async Task ExplicitClipboardSend_DoesNotRequireAutomaticSync()
    {
        var transport = new FakeFeatureTransport();
        var manager = new SharingManager(transport) { ClipboardEnabled = false };

        await manager.SendClipboardNowAsync("send explicitly");

        Assert.Equal(ProtocolMessageTypes.ClipboardUpdate, transport.LastSentType);
        var payload = ProtocolSerializer.DecodePayload<ClipboardUpdatePayload>(transport.LastSentPayload!.Value);
        Assert.True(payload.IsManual);
        Assert.Single(manager.History);
        Assert.Equal("send explicitly", manager.History[0].Text);
    }

    [Fact]
    public void ClipboardHistory_DeduplicatesTextAndCanBeCleared()
    {
        var transport = new FakeFeatureTransport();
        var manager = new SharingManager(transport) { ClipboardEnabled = true, IsDeviceAllowed = _ => true };

        foreach (var revision in new[] { "revision-1", "revision-2" })
        {
            transport.Route(ProtocolMessageTypes.ClipboardUpdate, ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
            {
                RevisionId = revision, OriginDeviceId = "android", ContentType = "text/plain",
                Text = "same text", CreatedAtUtc = "2026-07-14T00:00:00Z",
            }));
        }

        Assert.Single(manager.History);
        Assert.Equal("revision-1", manager.History[0].ItemId);
        manager.ClearHistory();
        Assert.Empty(manager.History);
    }

    [Fact]
    public void ExplicitIncomingClipboard_IsAppliedWhenAutomaticSyncIsDisabled()
    {
        var transport = new FakeFeatureTransport();
        var manager = new SharingManager(transport) { ClipboardEnabled = false };
        var received = new List<string>();
        manager.ClipboardReceived += received.Add;

        transport.Route(ProtocolMessageTypes.ClipboardUpdate, ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
        {
            RevisionId = "manual-1", OriginDeviceId = "android", ContentType = "text/plain",
            Text = "manual text", CreatedAtUtc = "2026-07-14T00:00:00Z", IsManual = true,
        }));

        Assert.Equal(["manual text"], received);
    }

    [Fact]
    public void AutomaticIncomingClipboard_RequiresPerDeviceAuthorization()
    {
        var transport = new FakeFeatureTransport();
        var manager = new SharingManager(transport) { ClipboardEnabled = true };
        var received = new List<string>();
        manager.ClipboardReceived += received.Add;

        transport.Route(ProtocolMessageTypes.ClipboardUpdate, ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
        {
            RevisionId = "automatic-denied", OriginDeviceId = "android", ContentType = "text/plain",
            Text = "must not apply", CreatedAtUtc = "2026-07-15T00:00:00Z",
        }));

        Assert.Empty(received);
    }

    [Fact]
    public void IncomingClipboard_RejectsSpoofedOriginAndEmptyText()
    {
        var transport = new FakeFeatureTransport();
        _ = new SharingManager(transport) { ClipboardEnabled = true, IsDeviceAllowed = _ => true };

        Assert.Throws<InvalidDataException>(() => transport.Route(
            ProtocolMessageTypes.ClipboardUpdate,
            ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
            {
                RevisionId = "spoofed", OriginDeviceId = "another-device", ContentType = "text/plain",
                Text = "bad", CreatedAtUtc = "2026-07-15T00:00:00Z",
            })));
        Assert.Throws<InvalidDataException>(() => transport.Route(
            ProtocolMessageTypes.ClipboardUpdate,
            ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
            {
                RevisionId = "empty", OriginDeviceId = "android", ContentType = "text/plain",
                Text = " ", CreatedAtUtc = "2026-07-15T00:00:00Z",
            })));
    }

    private sealed class FakeFeatureTransport : IFeatureMessageTransport
    {
        public bool IsConnected { get; set; } = true;
        public string? LocalDeviceId => "windows";
        public string? RemoteDeviceId => "android";
        public string? LastSentType { get; private set; }
        public JsonElement? LastSentPayload { get; private set; }
        public event EventHandler<FeatureMessageEventArgs>? MessageReceived;
        public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
        {
            LastSentType = type;
            LastSentPayload = payload;
            return Task.CompletedTask;
        }
        public void Route(string type, JsonElement payload) => MessageReceived?.Invoke(this, new(type, payload));
    }
}
