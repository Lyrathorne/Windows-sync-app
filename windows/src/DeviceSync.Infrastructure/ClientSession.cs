using System.Threading.Channels;
using System.Net.Sockets;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Microsoft.Extensions.Logging;

namespace DeviceSync.Infrastructure;

public sealed class ClientSession : IDeviceMessageWriter
{
    private readonly TcpClient _client;
    private readonly ConnectionHandshakeHandler _handshakeHandler;
    private readonly HeartbeatResponder _heartbeatResponder;
    private readonly DeviceSessionRegistry _registry;
    private readonly ILogger _logger;
    private readonly Channel<ProtocolMessage> _outgoing = Channel.CreateUnbounded<ProtocolMessage>();
    private readonly CancellationTokenSource _sessionCts = new();
    private int _cleanupStarted;
    private string? _deviceId;
    private string? _windowsDeviceId;
    private DeviceSessionInfo? _registeredSession;

    public ClientSession(
        TcpClient client,
        ConnectionHandshakeHandler handshakeHandler,
        HeartbeatResponder heartbeatResponder,
        DeviceSessionRegistry registry,
        ILogger logger)
    {
        _client = client;
        _handshakeHandler = handshakeHandler;
        _heartbeatResponder = heartbeatResponder;
        _registry = registry;
        _logger = logger;
    }

    public event EventHandler<DeviceSessionChangedEventArgs>? SessionChanged;
    public event EventHandler? SessionAccepted;

    public async Task RunAsync(CancellationToken serverCancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken, _sessionCts.Token);
        try
        {
            await using var stream = _client.GetStream();
            var reader = new ProtocolFrameReader(stream);
            var writer = new ProtocolFrameWriter(stream);

            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, handshakeTimeout.Token);
            var hello = await reader.ReadAsync(handshakeCts.Token).ConfigureAwait(false);
            var handshake = await _handshakeHandler.HandleAsync(hello, linkedCts.Token).ConfigureAwait(false);

            if (!_registry.TryReplaceOrAdd(handshake.Session, out var replaced))
            {
                throw new ProtocolException("A different Android device is already connected.");
            }

            if (replaced is not null)
            {
                _logger.LogInformation("Replacing active session for {DeviceId}", replaced.DeviceId);
            }

            _deviceId = handshake.Session.DeviceId;
            _windowsDeviceId = handshake.HelloAck.SenderDeviceId;
            _registeredSession = handshake.Session;
            SessionAccepted?.Invoke(this, EventArgs.Empty);
            SessionChanged?.Invoke(this, new DeviceSessionChangedEventArgs(handshake.Session));
            await writer.WriteAsync(handshake.HelloAck, linkedCts.Token).ConfigureAwait(false);

            var writerTask = WriterLoopAsync(writer, linkedCts.Token);
            var readerTask = ReaderLoopAsync(reader, linkedCts.Token);
            await Task.WhenAny(writerTask, readerTask).ConfigureAwait(false);
            _sessionCts.Cancel();
            await Task.WhenAll(SuppressAsync(writerTask), SuppressAsync(readerTask)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Client session ended with an error.");
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    public async Task EnqueueAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        await _outgoing.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _sessionCts.Cancel();
        _client.Close();
        await CleanupAsync().ConfigureAwait(false);
        await Task.CompletedTask;
    }

    private async Task ReaderLoopAsync(ProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (_deviceId is not null && message.SenderDeviceId != _deviceId)
            {
                continue;
            }

            switch (message.Type)
            {
                case ProtocolMessageTypes.ConnectionPing:
                    await EnqueueAsync(await _heartbeatResponder.BuildPongAsync(message, cancellationToken).ConfigureAwait(false), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ProtocolMessageTypes.ConnectionClose:
                    _sessionCts.Cancel();
                    return;
                case ProtocolMessageTypes.MessageAck:
                case ProtocolMessageTypes.ConnectionPong:
                case ProtocolMessageTypes.ProtocolError:
                    break;
                default:
                    if (message.RequiresAcknowledgement)
                    {
                        await EnqueueAsync(BuildAck(message, "unsupported"), cancellationToken).ConfigureAwait(false);
                    }

                    break;
            }
        }
    }

    private async Task WriterLoopAsync(ProtocolFrameWriter writer, CancellationToken cancellationToken)
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private ProtocolMessage BuildAck(ProtocolMessage message, string status)
    {
        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.MessageAck,
            SenderDeviceId = _windowsDeviceId ?? throw new InvalidOperationException("Cannot ACK before handshake completes."),
            RecipientDeviceId = message.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = message.MessageId,
            RequiresAcknowledgement = false,
            Payload = ProtocolSerializer.PayloadToJson(new MessageAckPayload { Status = status }),
        };
    }

    private async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) == 1)
        {
            return;
        }

        _sessionCts.Cancel();
        _outgoing.Writer.TryComplete();
        _client.Close();
        if (_registeredSession is not null)
        {
            _registry.Remove(_registeredSession);
        }

        SessionChanged?.Invoke(this, new DeviceSessionChangedEventArgs(null));
        await Task.CompletedTask;
    }

    private static async Task SuppressAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
