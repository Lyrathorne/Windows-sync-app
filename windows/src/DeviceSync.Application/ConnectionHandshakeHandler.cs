using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed class ConnectionHandshakeHandler
{
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private readonly string _windowsDeviceName;

    public ConnectionHandshakeHandler(IWindowsDeviceIdentityProvider identityProvider, string? windowsDeviceName = null)
    {
        _identityProvider = identityProvider;
        _windowsDeviceName = string.IsNullOrWhiteSpace(windowsDeviceName)
            ? Environment.MachineName
            : windowsDeviceName;
    }

    public async Task<HandshakeResult> HandleAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Type != ProtocolMessageTypes.ConnectionHello)
        {
            throw new ProtocolException("The first message must be connection.hello.");
        }

        if (message.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            throw new ProtocolException("Unsupported protocol version.");
        }

        if (string.IsNullOrWhiteSpace(message.MessageId) || string.IsNullOrWhiteSpace(message.SenderDeviceId))
        {
            throw new ProtocolException("Hello message is missing identifiers.");
        }

        var payload = ProtocolSerializer.DecodePayload<ConnectionHelloPayload>(message.Payload);
        if (string.IsNullOrWhiteSpace(payload.DeviceName))
        {
            throw new ProtocolException("Android device name is required.");
        }

        if (!string.Equals(payload.DeviceType, "android", StringComparison.Ordinal))
        {
            throw new ProtocolException("Only android hello messages are accepted.");
        }

        var negotiatedVersion = ProtocolVersionNegotiator.Negotiate(
            payload.ProtocolVersion,
            payload.ProtocolMin,
            payload.ProtocolMax);
        if (negotiatedVersion is null)
        {
            throw new ProtocolException("Hello payload protocol version is unsupported.");
        }

        var windowsDeviceId = await _identityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var session = new DeviceSessionInfo
        {
            DeviceId = message.SenderDeviceId,
            DeviceName = payload.DeviceName,
            DeviceType = payload.DeviceType,
            ProtocolVersion = negotiatedVersion.Value,
            Capabilities = CapabilityNegotiator.Intersect(payload.Capabilities),
            ConnectedAtUtc = now,
            LastSeenAtUtc = now,
        };

        var ackPayload = new ConnectionHelloAckPayload
        {
            DeviceName = _windowsDeviceName,
            DeviceType = "windows",
            AcceptedProtocolVersion = negotiatedVersion.Value,
            ProtocolMin = ProtocolConstants.ProtocolMinVersion,
            ProtocolMax = ProtocolConstants.ProtocolMaxVersion,
            MaxFrameBytes = ProtocolConstants.MaxJsonMessageSize + ProtocolConstants.FrameHeaderSize,
            MaxPayloadBytes = ProtocolConstants.MaxJsonPayloadSize,
            Capabilities = SupportedCapabilities.Values,
        };

        var ack = new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.ConnectionHelloAck,
            SenderDeviceId = windowsDeviceId,
            RecipientDeviceId = message.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = message.MessageId,
            RequiresAcknowledgement = false,
            Payload = ProtocolSerializer.PayloadToJson(ackPayload),
        };

        return new HandshakeResult(session, ack);
    }
}

public sealed record HandshakeResult(DeviceSessionInfo Session, ProtocolMessage HelloAck);
