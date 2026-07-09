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
    private readonly AuthHandshakeHandler? _authHandshakeHandler;
    private readonly PairingRequestHandler? _pairingRequestHandler;
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
        AuthHandshakeHandler? authHandshakeHandler,
        PairingRequestHandler? pairingRequestHandler,
        HeartbeatResponder heartbeatResponder,
        DeviceSessionRegistry registry,
        ILogger logger)
    {
        _client = client;
        _handshakeHandler = handshakeHandler;
        _authHandshakeHandler = authHandshakeHandler;
        _pairingRequestHandler = pairingRequestHandler;
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
            var firstMessage = await reader.ReadAsync(handshakeCts.Token).ConfigureAwait(false);
            _logger.LogInformation("FIRST_FRAME_RECEIVED {MessageType}", firstMessage.Type);
            if (firstMessage.Type == ProtocolMessageTypes.PairingRequest)
            {
                _logger.LogInformation("PAIRING_REQUEST_ROUTED");
                await HandlePairingRequestAsync(firstMessage, reader, writer, linkedCts.Token).ConfigureAwait(false);
                return;
            }

            if (firstMessage.Type != ProtocolMessageTypes.ConnectionHello)
            {
                await writer.WriteAsync(BuildProtocolError(firstMessage, "EXPECTED_HELLO_OR_PAIRING_REQUEST"), linkedCts.Token)
                    .ConfigureAwait(false);
                return;
            }

            var handshake = await HandleAuthenticatedHelloAsync(firstMessage, reader, writer, linkedCts.Token).ConfigureAwait(false);
            if (handshake is null)
            {
                return;
            }

            if (!_registry.TryReplaceOrAdd(handshake.Session, out var replaced))
            {
                throw new ProtocolException("A different Android device is already connected.");
            }

            if (replaced is not null)
            {
                _logger.LogInformation("Replacing active session for {DeviceId}", replaced.DeviceId);
            }

            _deviceId = handshake.Session.DeviceId;
            _windowsDeviceId = handshake.Accepted.SenderDeviceId;
            _registeredSession = handshake.Session;
            SessionAccepted?.Invoke(this, EventArgs.Empty);
            SessionChanged?.Invoke(this, new DeviceSessionChangedEventArgs(handshake.Session));
            await writer.WriteAsync(handshake.Accepted, linkedCts.Token).ConfigureAwait(false);

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

    private async Task<AuthenticatedHandshakeResult?> HandleAuthenticatedHelloAsync(
        ProtocolMessage hello,
        ProtocolFrameReader reader,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        if (_authHandshakeHandler is null)
        {
            var legacy = await _handshakeHandler.HandleAsync(hello, cancellationToken).ConfigureAwait(false);
            return new AuthenticatedHandshakeResult(legacy.Session, legacy.HelloAck);
        }

        var challenge = await _authHandshakeHandler.BuildChallengeAsync(hello, cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(challenge.Response, cancellationToken).ConfigureAwait(false);
        if (challenge.Attempt is null)
        {
            return null;
        }

        using var authTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authTimeout.Token);
        var response = await reader.ReadAsync(authCts.Token).ConfigureAwait(false);
        var verification = await _authHandshakeHandler.VerifyResponseAsync(challenge.Attempt, response, cancellationToken).ConfigureAwait(false);
        if (!verification.IsAccepted)
        {
            await writer.WriteAsync(verification.Response, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new AuthenticatedHandshakeResult(verification.Session!, verification.Response);
    }

    private async Task HandlePairingRequestAsync(
        ProtocolMessage message,
        ProtocolFrameReader reader,
        ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        if (_pairingRequestHandler is null)
        {
            await writer.WriteAsync(BuildProtocolError(message, "PAIRING_UNAVAILABLE"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await _pairingRequestHandler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
        {
            _logger.LogInformation("PAIRING_REQUEST_VALIDATED");
            _logger.LogInformation("PAIRING_CHALLENGE_QUEUED");
        }
        else
        {
            _logger.LogInformation("PAIRING_REQUEST_REJECTED {Code}", result.Code ?? "UNKNOWN");
        }
        await writer.WriteAsync(result.Response, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
        {
            _logger.LogInformation("PAIRING_CHALLENGE_SENT");
        }
        if (!result.Accepted)
        {
            return;
        }

        using var confirmTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var confirmCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, confirmTimeout.Token);
        var confirm = await reader.ReadAsync(confirmCts.Token).ConfigureAwait(false);
        if (confirm.Type == ProtocolMessageTypes.PairingCancel)
        {
            return;
        }

        var confirmResponse = await _pairingRequestHandler.HandleConfirmAsync(confirm, cancellationToken).ConfigureAwait(false);
        if (confirmResponse.Type == ProtocolMessageTypes.PairingRejected)
        {
            await writer.WriteAsync(confirmResponse, cancellationToken).ConfigureAwait(false);
            return;
        }

        var confirmPayload = ProtocolSerializer.DecodePayload<PairingConfirmPayload>(confirm.Payload);
        ProtocolMessage accepted;
        if (confirmResponse.Type == ProtocolMessageTypes.PairingAccepted)
        {
            accepted = confirmResponse;
        }
        else
        {
            while (!_pairingRequestHandler.CanBuildAccepted(confirmPayload.SessionId))
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            accepted = await _pairingRequestHandler.BuildAcceptedForCurrentSessionAsync(confirm, cancellationToken).ConfigureAwait(false);
        }
        await writer.WriteAsync(accepted, cancellationToken).ConfigureAwait(false);

        try
        {
            using var ackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ackTimeout.Token);
            var ack = await reader.ReadAsync(ackCts.Token).ConfigureAwait(false);
            if (!await _pairingRequestHandler.HandleCompleteAckAsync(ack, cancellationToken).ConfigureAwait(false))
            {
                await _pairingRequestHandler.ExpirePendingTrustAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await _pairingRequestHandler.ExpirePendingTrustAsync(CancellationToken.None).ConfigureAwait(false);
        }
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
                case ProtocolMessageTypes.AuthResponse:
                    await EnqueueAsync(BuildProtocolError(message, "AUTH_ALREADY_ACCEPTED"), cancellationToken).ConfigureAwait(false);
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

    private ProtocolMessage BuildProtocolError(ProtocolMessage message, string code)
    {
        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.ProtocolError,
            SenderDeviceId = _windowsDeviceId ?? "windows",
            RecipientDeviceId = message.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = message.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new ProtocolErrorPayload
            {
                Code = code,
                Message = "Unexpected first message.",
                Fatal = true,
            }),
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

internal sealed record AuthenticatedHandshakeResult(DeviceSessionInfo Session, ProtocolMessage Accepted);
