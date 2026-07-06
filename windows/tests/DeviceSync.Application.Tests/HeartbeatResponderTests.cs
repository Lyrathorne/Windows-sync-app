using DeviceSync.Application;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class HeartbeatResponderTests
{
    [Fact]
    public async Task ValidPing_CreatesMatchingPong()
    {
        var responder = new HeartbeatResponder(new FakeIdentityProvider("windows-fixed"));
        var ping = Messages.Ping();

        var pong = await responder.BuildPongAsync(ping);
        var payload = ProtocolSerializer.DecodePayload<PongPayload>(pong.Payload);

        Assert.Equal(ProtocolMessageTypes.ConnectionPong, pong.Type);
        Assert.Equal(42, payload.Sequence);
        Assert.Equal("ping-1", pong.CorrelationId);
        Assert.Equal("windows-fixed", pong.SenderDeviceId);
        Assert.Equal("android-1", pong.RecipientDeviceId);
    }
}
