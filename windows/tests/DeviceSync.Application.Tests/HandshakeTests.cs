using DeviceSync.Application;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class HandshakeTests
{
    [Fact]
    public async Task ValidHello_CreatesSessionAndHelloAck()
    {
        var handler = new ConnectionHandshakeHandler(new FakeIdentityProvider("windows-fixed"), "Windows PC");
        var hello = Messages.Hello();

        var result = await handler.HandleAsync(hello);

        Assert.Equal("android-1", result.Session.DeviceId);
        Assert.Equal(ProtocolMessageTypes.ConnectionHelloAck, result.HelloAck.Type);
        Assert.Equal("hello-1", result.HelloAck.CorrelationId);
        Assert.Equal("windows-fixed", result.HelloAck.SenderDeviceId);
    }

    [Fact]
    public async Task WrongProtocolVersion_IsRejected()
    {
        var handler = new ConnectionHandshakeHandler(new FakeIdentityProvider("windows-fixed"));
        var hello = Messages.Hello() with { ProtocolVersion = 2 };

        await Assert.ThrowsAsync<ProtocolException>(() => handler.HandleAsync(hello));
    }

    [Fact]
    public async Task PingFirst_IsRejected()
    {
        var handler = new ConnectionHandshakeHandler(new FakeIdentityProvider("windows-fixed"));

        await Assert.ThrowsAsync<ProtocolException>(() => handler.HandleAsync(Messages.Ping()));
    }

    [Fact]
    public async Task EmptySenderDeviceId_IsRejected()
    {
        var handler = new ConnectionHandshakeHandler(new FakeIdentityProvider("windows-fixed"));
        var hello = Messages.Hello() with { SenderDeviceId = "" };

        await Assert.ThrowsAsync<ProtocolException>(() => handler.HandleAsync(hello));
    }

    [Fact]
    public async Task EmptyDeviceName_IsRejected()
    {
        var handler = new ConnectionHandshakeHandler(new FakeIdentityProvider("windows-fixed"));
        var hello = Messages.Hello(deviceName: "");

        await Assert.ThrowsAsync<ProtocolException>(() => handler.HandleAsync(hello));
    }

    [Fact]
    public async Task WrongDeviceType_IsRejected()
    {
        var handler = new ConnectionHandshakeHandler(new FakeIdentityProvider("windows-fixed"));
        var hello = Messages.Hello(deviceType: "ios");

        await Assert.ThrowsAsync<ProtocolException>(() => handler.HandleAsync(hello));
    }
}
