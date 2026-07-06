using System.Text.Json;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class ProtocolSerializerTests
{
    [Fact]
    public void SerializeHello_UsesAndroidCompatibleCamelCase()
    {
        var message = TestMessages.AndroidHello();

        var json = ProtocolSerializer.Serialize(message);

        Assert.Contains("\"protocolVersion\":1", json);
        Assert.Contains("\"type\":\"connection.hello\"", json);
        Assert.Contains("\"senderDeviceId\":\"android-test-device\"", json);
        Assert.DoesNotContain("ProtocolVersion", json);
    }

    [Fact]
    public void DeserializeAndroidHello_IgnoresUnknownFields()
    {
        var json = """
        {"protocolVersion":1,"messageId":"hello-1","type":"connection.hello","senderDeviceId":"android-test-device","timestampUtc":"2026-07-05T18:45:00Z","requiresAcknowledgement":true,"payload":{"deviceName":"Pixel","deviceType":"android","appVersion":"1.0","protocolVersion":1,"capabilities":["heartbeat-v1"],"extra":true},"unknown":42}
        """;

        var message = ProtocolSerializer.Deserialize(json);
        var payload = ProtocolSerializer.DecodePayload<ConnectionHelloPayload>(message.Payload);

        Assert.Equal(ProtocolMessageTypes.ConnectionHello, message.Type);
        Assert.Equal("Pixel", payload.DeviceName);
    }

    [Fact]
    public void PayloadRoundTrip_PreservesUnicode()
    {
        var payload = new ConnectionHelloPayload
        {
            DeviceName = "Pixel Unicode",
            AppVersion = "1.0",
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
        };

        var json = ProtocolSerializer.PayloadToJson(payload);
        var decoded = ProtocolSerializer.DecodePayload<ConnectionHelloPayload>(json);

        Assert.Equal(payload.DeviceName, decoded.DeviceName);
    }

    [Fact]
    public void InvalidJson_IsRejected()
    {
        Assert.Throws<ProtocolException>(() => ProtocolSerializer.Deserialize("{bad json"));
    }

    [Fact]
    public void MessageTypes_MatchAndroidStrings()
    {
        Assert.Equal("connection.hello", ProtocolMessageTypes.ConnectionHello);
        Assert.Equal("connection.hello_ack", ProtocolMessageTypes.ConnectionHelloAck);
        Assert.Equal("connection.ping", ProtocolMessageTypes.ConnectionPing);
        Assert.Equal("connection.pong", ProtocolMessageTypes.ConnectionPong);
        Assert.Equal("connection.close", ProtocolMessageTypes.ConnectionClose);
        Assert.Equal("message.ack", ProtocolMessageTypes.MessageAck);
        Assert.Equal("error.protocol", ProtocolMessageTypes.ProtocolError);
    }

    [Fact]
    public void HelloAck_SerializesExpectedPayload()
    {
        var ack = TestMessages.WindowsHelloAck();
        var payload = ProtocolSerializer.DecodePayload<ConnectionHelloAckPayload>(ack.Payload);

        Assert.Equal("windows", payload.DeviceType);
        Assert.Equal(ProtocolConstants.ProtocolVersion, payload.AcceptedProtocolVersion);
        Assert.Contains("heartbeat-v1", payload.Capabilities);
    }

    [Fact]
    public void PingPongPayloads_RoundTrip()
    {
        var ping = ProtocolSerializer.PayloadToJson(new PingPayload { Sequence = 7, SentAtUtc = "2026-07-05T18:45:02Z" });
        var pong = ProtocolSerializer.PayloadToJson(new PongPayload { Sequence = 7, ReceivedAtUtc = "2026-07-05T18:45:03Z" });

        Assert.Equal(7, ProtocolSerializer.DecodePayload<PingPayload>(ping).Sequence);
        Assert.Equal(7, ProtocolSerializer.DecodePayload<PongPayload>(pong).Sequence);
    }
}
