using System.Text.Json;
using DeviceSync.Application;
using DeviceSync.Protocol;

namespace DeviceSync.Infrastructure;

public sealed class FeatureMessageTransport : IFeatureMessageTransport
{
    private readonly object _gate = new();
    private IDeviceMessageWriter? _writer;
    private string? _remoteDeviceId;
    public bool IsConnected { get { lock (_gate) return _writer is not null; } }
    public string? LocalDeviceId { get; private set; }
    public event EventHandler<FeatureMessageEventArgs>? MessageReceived;

    internal void Attach(IDeviceMessageWriter writer, string localDeviceId, string remoteDeviceId)
    {
        lock (_gate) { _writer = writer; LocalDeviceId = localDeviceId; _remoteDeviceId = remoteDeviceId; }
    }
    internal void Detach(IDeviceMessageWriter writer)
    {
        lock (_gate) if (ReferenceEquals(_writer, writer)) { _writer = null; LocalDeviceId = null; _remoteDeviceId = null; }
    }
    internal void Route(string type, JsonElement payload) => MessageReceived?.Invoke(this, new(type, payload));
    public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
    {
        IDeviceMessageWriter writer;
        string local;
        string remote;
        lock (_gate) { writer = _writer ?? throw new InvalidOperationException("No authenticated device is connected."); local = LocalDeviceId!; remote = _remoteDeviceId!; }
        return writer.EnqueueAsync(new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = type,
            SenderDeviceId = local,
            RecipientDeviceId = remote,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            Payload = payload,
        }, cancellationToken);
    }
}
