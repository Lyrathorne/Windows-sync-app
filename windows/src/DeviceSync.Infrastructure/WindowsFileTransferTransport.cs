using System.Text.Json;
using DeviceSync.Application;
using DeviceSync.Protocol;

namespace DeviceSync.Infrastructure;

public sealed class WindowsFileTransferTransport : IOutgoingFileTransferTransport
{
    private readonly object _gate = new();
    private IDeviceMessageWriter? _writer;
    private string? _localDeviceId;
    public bool IsConnected { get { lock (_gate) return _writer is not null; } }
    public string? RemoteDeviceId { get; private set; }
    public bool SupportsResumableTransfers { get; private set; }
    public event EventHandler<OutgoingFileTransferMessageEventArgs>? MessageReceived;
    public event EventHandler? Disconnected;

    internal void Attach(IDeviceMessageWriter writer, string localDeviceId, string remoteDeviceId, IReadOnlyList<string> capabilities)
    {
        lock (_gate)
        {
            _writer = writer;
            _localDeviceId = localDeviceId;
            RemoteDeviceId = remoteDeviceId;
            SupportsResumableTransfers = capabilities.Contains(SupportedCapabilities.FileTransferV2);
        }
    }

    internal void Detach(IDeviceMessageWriter writer)
    {
        var changed = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_writer, writer)) return;
            _writer = null;
            _localDeviceId = null;
            RemoteDeviceId = null;
            SupportsResumableTransfers = false;
            changed = true;
        }
        if (changed) Disconnected?.Invoke(this, EventArgs.Empty);
    }

    internal void Route(string type, JsonElement payload) => MessageReceived?.Invoke(this, new(type, payload));

    public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
    {
        IDeviceMessageWriter writer;
        string local;
        string remote;
        lock (_gate)
        {
            writer = _writer ?? throw new InvalidOperationException("No authenticated device is connected.");
            local = _localDeviceId!;
            remote = RemoteDeviceId!;
        }
        return writer.EnqueueAsync(new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = type,
            SenderDeviceId = local,
            RecipientDeviceId = remote,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            RequiresAcknowledgement = false,
            Payload = payload,
        }, cancellationToken);
    }
}
