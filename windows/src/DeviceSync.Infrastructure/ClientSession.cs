using System.Threading.Channels;
using System.Net.Sockets;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Microsoft.Extensions.Logging;

namespace DeviceSync.Infrastructure;

public sealed class ClientSession : IDeviceMessageWriter
{
    private readonly Stream _stream;
    private readonly Func<ValueTask> _closeTransport;
    private readonly DeviceTransportEndpoint _endpoint;
    private readonly DeviceTransportProfile _transportProfile;
    private readonly RecentMessageDeduplicator _deduplicator;
    private readonly TransportSessionCoordinator? _sessionCoordinator;
    private readonly ConnectionHandshakeHandler _handshakeHandler;
    private readonly AuthHandshakeHandler? _authHandshakeHandler;
    private readonly PairingRequestHandler? _pairingRequestHandler;
    private readonly HeartbeatResponder _heartbeatResponder;
    private readonly DeviceSessionRegistry _registry;
    private readonly ILogger _logger;
    private readonly IncomingFileTransferManager? _incomingFileTransferManager;
    private readonly WindowsFileTransferTransport? _outgoingFileTransferTransport;
    private readonly FeatureMessageTransport? _featureMessageTransport;
    private readonly Channel<ProtocolMessage> _outgoing = Channel.CreateBounded<ProtocolMessage>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
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
        ILogger logger,
        IncomingFileTransferManager? incomingFileTransferManager = null,
        Stream? transportStream = null,
        WindowsFileTransferTransport? outgoingFileTransferTransport = null,
        FeatureMessageTransport? featureMessageTransport = null,
        TransportSessionCoordinator? sessionCoordinator = null)
        : this(
            transportStream ?? client.GetStream(),
            () =>
            {
                client.Close();
                return ValueTask.CompletedTask;
            },
            new DeviceTransportEndpoint(
                DeviceTransportKind.Lan,
                client.Client.RemoteEndPoint?.ToString() ?? "unknown"),
            handshakeHandler,
            authHandshakeHandler,
            pairingRequestHandler,
            heartbeatResponder,
            registry,
            logger,
            incomingFileTransferManager,
            outgoingFileTransferTransport,
            featureMessageTransport,
            sessionCoordinator: sessionCoordinator)
    {
    }

    public ClientSession(
        Stream stream,
        Func<ValueTask> closeTransport,
        DeviceTransportEndpoint endpoint,
        ConnectionHandshakeHandler handshakeHandler,
        AuthHandshakeHandler? authHandshakeHandler,
        PairingRequestHandler? pairingRequestHandler,
        HeartbeatResponder heartbeatResponder,
        DeviceSessionRegistry registry,
        ILogger logger,
        IncomingFileTransferManager? incomingFileTransferManager = null,
        WindowsFileTransferTransport? outgoingFileTransferTransport = null,
        FeatureMessageTransport? featureMessageTransport = null,
        RecentMessageDeduplicator? deduplicator = null,
        TransportSessionCoordinator? sessionCoordinator = null)
    {
        _stream = stream;
        _closeTransport = closeTransport;
        _endpoint = endpoint;
        _transportProfile = DeviceTransportProfile.For(endpoint.Kind);
        _deduplicator = deduplicator ?? new RecentMessageDeduplicator();
        _sessionCoordinator = sessionCoordinator;
        _handshakeHandler = handshakeHandler;
        _authHandshakeHandler = authHandshakeHandler;
        _pairingRequestHandler = pairingRequestHandler;
        _heartbeatResponder = heartbeatResponder;
        _registry = registry;
        _logger = logger;
        _incomingFileTransferManager = incomingFileTransferManager;
        _outgoingFileTransferTransport = outgoingFileTransferTransport;
        _featureMessageTransport = featureMessageTransport;
    }

    public event EventHandler<DeviceSessionChangedEventArgs>? SessionChanged;
    public event EventHandler? SessionAccepted;
    public string? DeviceId => _deviceId;
    public DeviceTransportKind TransportKind => _endpoint.Kind;

    public async Task RunAsync(CancellationToken serverCancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken, _sessionCts.Token);
        try
        {
            await using var stream = _stream;
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

            _deviceId = handshake.Session.DeviceId;
            _windowsDeviceId = handshake.Accepted.SenderDeviceId;
            _registeredSession = handshake.Session with
            {
                TransportKind = _endpoint.Kind,
                IsSlowTransport = _transportProfile.IsSlow,
                TransportAddress = _endpoint.Address,
                Capabilities = handshake.Session.Capabilities
                    .Where(capability => !_transportProfile.DisabledCapabilities.Contains(capability))
                    .ToArray(),
            };
            if (_sessionCoordinator is not null)
            {
                if (!_sessionCoordinator.TryActivate(this, out var replacedSession))
                    throw new ProtocolException("A higher-priority transport is already active.");
                if (replacedSession is not null && !ReferenceEquals(replacedSession, this))
                    _ = replacedSession.StopAsync();
            }
            if (!_registry.TryReplaceOrAdd(_registeredSession, out var replaced))
                throw new ProtocolException("A different Android device is already connected.");
            if (replaced is not null)
                _logger.LogInformation("Replacing active session for {DeviceId}", replaced.DeviceId);
            if (_incomingFileTransferManager is not null)
            {
                _incomingFileTransferManager.ResponseRequested += OnFileTransferResponseRequested;
            }
            SessionAccepted?.Invoke(this, EventArgs.Empty);
            _outgoingFileTransferTransport?.Attach(this, _windowsDeviceId!, _deviceId!, _registeredSession?.Capabilities ?? []);
            _featureMessageTransport?.Attach(this, _windowsDeviceId!, _deviceId!, _registeredSession?.Capabilities ?? []);
            SessionChanged?.Invoke(this, new DeviceSessionChangedEventArgs(_registeredSession));
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
        await _closeTransport().ConfigureAwait(false);
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
                if (!_pairingRequestHandler.CanContinueWaitingForAccepted(confirmPayload.SessionId))
                {
                    await writer.WriteAsync(
                        _pairingRequestHandler.BuildPairingWaitEnded(confirm),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
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
            if (!_deduplicator.TryAccept(message.SenderDeviceId, message.MessageId, DateTimeOffset.UtcNow))
            {
                if (message.RequiresAcknowledgement)
                    await EnqueueAsync(BuildAck(message, "duplicate"), cancellationToken).ConfigureAwait(false);
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
                case ProtocolMessageTypes.FileOffer:
                    if (_incomingFileTransferManager is not null)
                    {
                        var offer = ProtocolSerializer.DecodePayload<FileOfferPayload>(message.Payload);
                        if (offer.SizeBytes > _transportProfile.MaximumFileBytes)
                        {
                            await ProcessRequiredFileResponseAsync(
                                message,
                                Task.FromResult(new FileTransferResponse(
                                    ProtocolMessageTypes.FileReject,
                                    ProtocolSerializer.PayloadToJson(new FileRejectPayload
                                    {
                                        TransferId = offer.TransferId,
                                        Code = "transport_file_too_large",
                                        Message = $"This transport supports files up to {_transportProfile.MaximumFileBytes / (1024 * 1024)} MiB.",
                                    }))),
                                cancellationToken).ConfigureAwait(false);
                            break;
                        }
                        _ = ProcessFileResponseAsync(
                            message,
                            _incomingFileTransferManager.HandleOfferAsync(
                                message.SenderDeviceId,
                                offer,
                                cancellationToken,
                                resumable: _registeredSession?.Capabilities.Contains(SupportedCapabilities.FileTransferV2) == true),
                            cancellationToken);
                    }
                    break;
                case ProtocolMessageTypes.FileChunk:
                    if (_incomingFileTransferManager is not null)
                    {
                        await ProcessFileResponseAsync(
                            message,
                            _incomingFileTransferManager.HandleChunkAsync(
                                message.SenderDeviceId,
                                ProtocolSerializer.DecodePayload<FileChunkPayload>(message.Payload),
                                cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ProtocolMessageTypes.FileComplete:
                    if (_incomingFileTransferManager is not null)
                    {
                        await ProcessRequiredFileResponseAsync(
                            message,
                            _incomingFileTransferManager.HandleCompleteAsync(
                                message.SenderDeviceId,
                                ProtocolSerializer.DecodePayload<FileCompletePayload>(message.Payload),
                                cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ProtocolMessageTypes.FileResumeRequest:
                    if (_incomingFileTransferManager is not null)
                    {
                        await ProcessRequiredFileResponseAsync(
                            message,
                            _incomingFileTransferManager.HandleResumeRequestAsync(
                                message.SenderDeviceId,
                                ProtocolSerializer.DecodePayload<FileResumeRequestPayload>(message.Payload),
                                cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ProtocolMessageTypes.FileCancel:
                    _outgoingFileTransferTransport?.Route(message.Type, message.Payload);
                    if (_incomingFileTransferManager is not null)
                    {
                        await _incomingFileTransferManager.HandleCancelAsync(
                            message.SenderDeviceId,
                            ProtocolSerializer.DecodePayload<FileCancelPayload>(message.Payload),
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case ProtocolMessageTypes.FileAccept:
                case ProtocolMessageTypes.FileReject:
                case ProtocolMessageTypes.FileReceived:
                case ProtocolMessageTypes.FileError:
                case ProtocolMessageTypes.FileChunkReceived:
                case ProtocolMessageTypes.FileResumeAccepted:
                    _outgoingFileTransferTransport?.Route(message.Type, message.Payload);
                    break;
                case ProtocolMessageTypes.ClipboardUpdate:
                case ProtocolMessageTypes.TextShare:
                case ProtocolMessageTypes.NotificationPosted:
                case ProtocolMessageTypes.NotificationUpdated:
                case ProtocolMessageTypes.NotificationRemoved:
                case ProtocolMessageTypes.NotificationActionInvoke:
                case ProtocolMessageTypes.NotificationActionResult:
                case ProtocolMessageTypes.FolderManifest:
                        case ProtocolMessageTypes.FolderPlan:
                        case ProtocolMessageTypes.FolderPlanApproved:
                case ProtocolMessageTypes.FolderCancel:
                case ProtocolMessageTypes.FolderError:
                case ProtocolMessageTypes.CatalogPage:
                case ProtocolMessageTypes.CatalogChanged:
                case ProtocolMessageTypes.CatalogThumbnailResponse:
                case ProtocolMessageTypes.CatalogPermission:
                case ProtocolMessageTypes.CatalogError:
                case ProtocolMessageTypes.CatalogCancel:
                    _featureMessageTransport?.Route(message.Type, message.Payload);
                    if (message.RequiresAcknowledgement)
                    {
                        await EnqueueAsync(BuildAck(message, "processed"), cancellationToken).ConfigureAwait(false);
                    }
                    break;
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

    private async Task ProcessFileResponseAsync(
        ProtocolMessage request,
        Task<FileTransferResponse?> responseTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            if (response is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await EnqueueAsync(new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = response.Type,
                SenderDeviceId = _windowsDeviceId ?? throw new InvalidOperationException("File response requires an authenticated session."),
                RecipientDeviceId = request.SenderDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                CorrelationId = request.MessageId,
                RequiresAcknowledgement = false,
                Payload = response.Payload,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "FILE_TRANSFER_FAILED sender={SenderDeviceId}", request.SenderDeviceId);
        }
    }

    private Task ProcessRequiredFileResponseAsync(
        ProtocolMessage request,
        Task<FileTransferResponse> responseTask,
        CancellationToken cancellationToken)
        => ProcessFileResponseAsync(request, AwaitNullableAsync(responseTask), cancellationToken);

    private static async Task<FileTransferResponse?> AwaitNullableAsync(Task<FileTransferResponse> task)
        => await task.ConfigureAwait(false);

    private void OnFileTransferResponseRequested(object? sender, FileTransferResponseRequestedEventArgs args)
    {
        if (_deviceId is null || _windowsDeviceId is null || args.Transfer.SenderDeviceId != _deviceId)
        {
            return;
        }

        _ = EnqueueAsync(new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = args.Response.Type,
            SenderDeviceId = _windowsDeviceId,
            RecipientDeviceId = _deviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            RequiresAcknowledgement = false,
            Payload = args.Response.Payload,
        }, _sessionCts.Token);
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
        if (_incomingFileTransferManager is not null)
        {
            _incomingFileTransferManager.ResponseRequested -= OnFileTransferResponseRequested;
        }
        _outgoingFileTransferTransport?.Detach(this);
        _featureMessageTransport?.Detach(this);
        _sessionCoordinator?.Release(this);
        await _closeTransport().ConfigureAwait(false);
        if (_registeredSession is not null)
        {
            _registry.Remove(_registeredSession);
        }

        if (_incomingFileTransferManager is not null && _deviceId is not null)
        {
            await _incomingFileTransferManager.HandleDisconnectAsync(_deviceId).ConfigureAwait(false);
        }

        SessionChanged?.Invoke(this, new DeviceSessionChangedEventArgs(null));
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
