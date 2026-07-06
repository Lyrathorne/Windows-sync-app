using DeviceSync.Protocol;

namespace DeviceSync.Protocol.Tests;

internal static class TestMessages
{
    public static ProtocolMessage AndroidHello(string deviceId = "android-test-device", string deviceName = "Pixel") => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "hello-1",
        Type = ProtocolMessageTypes.ConnectionHello,
        SenderDeviceId = deviceId,
        TimestampUtc = "2026-07-05T18:45:00Z",
        RequiresAcknowledgement = true,
        Payload = ProtocolSerializer.PayloadToJson(new ConnectionHelloPayload
        {
            DeviceName = deviceName,
            AppVersion = "1.0",
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
        }),
    };

    public static ProtocolMessage WindowsHelloAck() => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "ack-1",
        Type = ProtocolMessageTypes.ConnectionHelloAck,
        SenderDeviceId = "windows-test-device",
        RecipientDeviceId = "android-test-device",
        TimestampUtc = "2026-07-05T18:45:01Z",
        CorrelationId = "hello-1",
        Payload = ProtocolSerializer.PayloadToJson(new ConnectionHelloAckPayload
        {
            DeviceName = "Windows PC",
            DeviceType = "windows",
            AcceptedProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
        }),
    };

    public static ProtocolMessage AndroidPing() => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "ping-1",
        Type = ProtocolMessageTypes.ConnectionPing,
        SenderDeviceId = "android-test-device",
        RecipientDeviceId = "windows-test-device",
        TimestampUtc = "2026-07-05T18:45:02Z",
        Payload = ProtocolSerializer.PayloadToJson(new PingPayload
        {
            Sequence = 1,
            SentAtUtc = "2026-07-05T18:45:02Z",
        }),
    };
}
