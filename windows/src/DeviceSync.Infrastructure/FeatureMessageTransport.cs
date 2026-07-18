using System.Text.Json;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceSync.Infrastructure;

public sealed class FeatureMessageTransport : IFeatureMessageTransport
{
    private readonly object _gate = new();
    private IDeviceMessageWriter? _writer;
    private string? _remoteDeviceId;
    private IReadOnlyCollection<string> _capabilities = [];
    private readonly ILogger<FeatureMessageTransport> _logger;

    public FeatureMessageTransport(ILogger<FeatureMessageTransport>? logger = null)
    {
        _logger = logger ?? NullLogger<FeatureMessageTransport>.Instance;
    }

    public bool IsConnected { get { lock (_gate) return _writer is not null; } }
    public string? LocalDeviceId { get; private set; }
    public string? RemoteDeviceId { get { lock (_gate) return _remoteDeviceId; } }
    public IReadOnlyCollection<string> Capabilities { get { lock (_gate) return _capabilities; } }
    public event EventHandler<FeatureMessageEventArgs>? MessageReceived;
    public event EventHandler? ConnectionChanged;

    internal void Attach(IDeviceMessageWriter writer, string localDeviceId, string remoteDeviceId, IReadOnlyCollection<string>? capabilities = null)
    {
        lock (_gate)
        {
            _writer = writer;
            LocalDeviceId = localDeviceId;
            _remoteDeviceId = remoteDeviceId;
            _capabilities = capabilities?.ToArray() ?? [];
        }
        _logger.LogInformation("FEATURE_TRANSPORT_ATTACHED remoteDeviceId={RemoteDeviceId}", remoteDeviceId);
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void Detach(IDeviceMessageWriter writer)
    {
        var changed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_writer, writer))
            {
                _writer = null;
                LocalDeviceId = null;
                _remoteDeviceId = null;
                _capabilities = [];
                changed = true;
            }
        }
        _logger.LogInformation("FEATURE_TRANSPORT_DETACHED");
        if (changed) ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void Route(string type, JsonElement payload)
    {
        _logger.LogInformation("FEATURE_MESSAGE_RECEIVED type={MessageType}", type);
        MessageReceived?.Invoke(this, new(type, payload));
    }
    public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
    {
        IDeviceMessageWriter writer;
        string local;
        string remote;
        lock (_gate) { writer = _writer ?? throw new InvalidOperationException("No authenticated device is connected."); local = LocalDeviceId!; remote = _remoteDeviceId!; }
        _logger.LogInformation("FEATURE_MESSAGE_QUEUED type={MessageType} remoteDeviceId={RemoteDeviceId}", type, remote);
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
