using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed class HeartbeatResponder
{
    private readonly IWindowsDeviceIdentityProvider _identityProvider;

    public HeartbeatResponder(IWindowsDeviceIdentityProvider identityProvider)
    {
        _identityProvider = identityProvider;
    }

    public async Task<ProtocolMessage> BuildPongAsync(ProtocolMessage ping, CancellationToken cancellationToken = default)
    {
        if (ping.Type != ProtocolMessageTypes.ConnectionPing)
        {
            throw new ProtocolException("Message is not connection.ping.");
        }

        var payload = ProtocolSerializer.DecodePayload<PingPayload>(ping.Payload);
        var windowsDeviceId = await _identityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);

        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.ConnectionPong,
            SenderDeviceId = windowsDeviceId,
            RecipientDeviceId = ping.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = ping.MessageId,
            RequiresAcknowledgement = false,
            Payload = ProtocolSerializer.PayloadToJson(new PongPayload
            {
                Sequence = payload.Sequence,
                ReceivedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            }),
        };
    }
}
