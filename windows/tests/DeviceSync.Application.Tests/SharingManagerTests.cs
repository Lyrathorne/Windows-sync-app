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
        var manager = new SharingManager(transport) { ClipboardEnabled = true };
        var received = new List<string>();
        manager.ClipboardReceived += received.Add;
        var payload = ProtocolSerializer.PayloadToJson(new ClipboardUpdatePayload
        {
            RevisionId = "revision-1", SourceDeviceId = "android", ContentType = "text/plain",
            Text = "hello", CreatedAtUtc = "2026-07-14T00:00:00Z",
        });

        transport.Route(ProtocolMessageTypes.ClipboardUpdate, payload);
        transport.Route(ProtocolMessageTypes.ClipboardUpdate, payload);

        Assert.Equal(["hello"], received);
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

    private sealed class FakeFeatureTransport : IFeatureMessageTransport
    {
        public bool IsConnected => true;
        public string? LocalDeviceId => "windows";
        public event EventHandler<FeatureMessageEventArgs>? MessageReceived;
        public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Route(string type, JsonElement payload) => MessageReceived?.Invoke(this, new(type, payload));
    }
}
