using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class ClipboardPayloadTests
{
    [Fact]
    public void SharedClipboardVector_UsesStableCrossPlatformFieldsAndIgnoresUnknownFields()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "clipboard", "clipboard-update-v1.json");
        var message = ProtocolSerializer.Deserialize(File.ReadAllText(path));
        var payload = ProtocolSerializer.DecodePayload<ClipboardUpdatePayload>(message.Payload);

        Assert.Equal(ProtocolMessageTypes.ClipboardUpdate, message.Type);
        Assert.Equal("android-vector", message.SenderDeviceId);
        Assert.Equal("018f-vector-revision", payload.RevisionId);
        Assert.Equal("android-vector", payload.OriginDeviceId);
        Assert.Equal("text/plain", payload.ContentType);
        Assert.Equal("Hello, общий буфер!", payload.Text);
        Assert.False(payload.IsManual);
    }
}
