using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceSync.Infrastructure;

public sealed class TcpDeviceServer : IDeviceServer, IAsyncDisposable
{
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private readonly IPairingSessionManager? _pairingSessionManager;
    private readonly IDeviceIdentityKeyProvider? _keyProvider;
    private readonly ITrustedDeviceRepository? _trustedDeviceRepository;
    private readonly DeviceSessionRegistry _registry;
    private readonly ILogger<TcpDeviceServer> _logger;
    private readonly IncomingFileTransferManager? _incomingFileTransferManager;
    private readonly ITlsCertificateProvider? _tlsCertificateProvider;
    private readonly WindowsFileTransferTransport? _outgoingFileTransferTransport;
    private readonly FeatureMessageTransport? _featureMessageTransport;
    private readonly TransportSessionCoordinator? _sessionCoordinator;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoop;
    private ClientSession? _activeClientSession;

    public TcpDeviceServer(
        IWindowsDeviceIdentityProvider identityProvider,
        DeviceSessionRegistry registry,
        IPairingSessionManager? pairingSessionManager = null,
        IDeviceIdentityKeyProvider? keyProvider = null,
        ITrustedDeviceRepository? trustedDeviceRepository = null,
        ILogger<TcpDeviceServer>? logger = null,
        IncomingFileTransferManager? incomingFileTransferManager = null,
        ITlsCertificateProvider? tlsCertificateProvider = null,
        WindowsFileTransferTransport? outgoingFileTransferTransport = null,
        FeatureMessageTransport? featureMessageTransport = null,
        TransportSessionCoordinator? sessionCoordinator = null)
    {
        _identityProvider = identityProvider;
        _pairingSessionManager = pairingSessionManager;
        _keyProvider = keyProvider;
        _trustedDeviceRepository = trustedDeviceRepository;
        _registry = registry;
        _logger = logger ?? NullLogger<TcpDeviceServer>.Instance;
        _incomingFileTransferManager = incomingFileTransferManager;
        _tlsCertificateProvider = tlsCertificateProvider;
        _outgoingFileTransferTransport = outgoingFileTransferTransport;
        _featureMessageTransport = featureMessageTransport;
        _sessionCoordinator = sessionCoordinator;
    }

    public int Port { get; private set; } = 54321;
    public bool IsRunning { get; private set; }
    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;
    public event EventHandler<DeviceSessionChangedEventArgs>? SessionChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        var settings = await _identityProvider.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        Port = settings.Port;
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _logger.LogInformation("TCP_LISTENER_STARTED address=0.0.0.0 port={Port}", Port);
        IsRunning = true;
        StateChanged?.Invoke(this, new ServerStateChangedEventArgs(true, Port, $"Listening on port {Port}"));
        _acceptLoop = AcceptLoopAsync(_serverCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        _serverCts?.Cancel();
        _listener?.Stop();
        await DisconnectActiveSessionAsync(cancellationToken).ConfigureAwait(false);

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }

        IsRunning = false;
        StateChanged?.Invoke(this, new ServerStateChangedEventArgs(false, Port, "Stopped"));
    }

    public async Task DisconnectActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        ClientSession? session;
        lock (_gate)
        {
            session = _activeClientSession;
        }

        if (session is not null)
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _registry.Clear();
        SessionChanged?.Invoke(this, new DeviceSessionChangedEventArgs(null));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _logger.LogInformation("TCP_CLIENT_ACCEPTED remote={Endpoint}", client.Client.RemoteEndPoint?.ToString() ?? "unknown");
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellationToken)
    {
        Stream? transportStream = null;
        if (_tlsCertificateProvider is not null)
        {
            try
            {
                var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                var certificate = await _tlsCertificateProvider.GetServerCertificateAsync(serverCancellationToken).ConfigureAwait(false);
                await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                }, serverCancellationToken).ConfigureAwait(false);
                transportStream = sslStream;
                _logger.LogInformation("TLS_SESSION_ESTABLISHED protocol={Protocol}", sslStream.SslProtocol);
            }
            catch (Exception error) when (error is AuthenticationException or IOException)
            {
                _logger.LogWarning(error, "TLS_HANDSHAKE_FAILED");
                client.Dispose();
                return;
            }
        }

        var session = new ClientSession(
            client,
            new ConnectionHandshakeHandler(_identityProvider),
            CreateAuthHandshakeHandler(),
            CreatePairingRequestHandler(),
            new HeartbeatResponder(_identityProvider),
            _registry,
            _logger,
            _incomingFileTransferManager,
            transportStream,
            _outgoingFileTransferTransport,
            _featureMessageTransport,
            _sessionCoordinator);

        session.SessionChanged += (_, args) => SessionChanged?.Invoke(this, args);
        session.SessionAccepted += (_, _) =>
        {
            ClientSession? previous;
            lock (_gate)
            {
                previous = _activeClientSession;
                _activeClientSession = session;
            }

            if (previous is not null && !ReferenceEquals(previous, session))
            {
                _ = previous.StopAsync();
            }
        };

        await session.RunAsync(serverCancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (ReferenceEquals(_activeClientSession, session))
            {
                _activeClientSession = null;
            }
        }
    }

    private PairingRequestHandler? CreatePairingRequestHandler()
    {
        if (_pairingSessionManager is null || _keyProvider is null || _trustedDeviceRepository is null)
        {
            return null;
        }

        return new PairingRequestHandler(_pairingSessionManager, _identityProvider, _keyProvider, _trustedDeviceRepository);
    }

    private AuthHandshakeHandler? CreateAuthHandshakeHandler()
    {
        if (_keyProvider is null || _trustedDeviceRepository is null)
        {
            return null;
        }

        return new AuthHandshakeHandler(
            _identityProvider,
            _keyProvider,
            _trustedDeviceRepository,
            pairingSessionManager: _pairingSessionManager);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _serverCts?.Dispose();
    }
}
