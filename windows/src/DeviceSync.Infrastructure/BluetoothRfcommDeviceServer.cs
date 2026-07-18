using System.Net.Security;
using System.Security.Authentication;
using DeviceSync.Application;
using InTheHand.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeviceSync.Infrastructure;

public sealed class BluetoothRfcommDeviceServer : IAsyncDisposable
{
    public static readonly Guid ServiceId = Guid.Parse("7d7d8f4a-6bd1-4d98-9b9c-5aa89f4a6210");
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private readonly IPairingSessionManager _pairingSessionManager;
    private readonly IDeviceIdentityKeyProvider _keyProvider;
    private readonly ITrustedDeviceRepository _trustedDevices;
    private readonly DeviceSessionRegistry _registry;
    private readonly IncomingFileTransferManager _incomingFiles;
    private readonly ITlsCertificateProvider _certificateProvider;
    private readonly WindowsFileTransferTransport _fileTransport;
    private readonly FeatureMessageTransport _featureTransport;
    private readonly TransportSessionCoordinator _sessionCoordinator;
    private readonly ILogger<BluetoothRfcommDeviceServer> _logger;
    private BluetoothListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public BluetoothRfcommDeviceServer(
        IWindowsDeviceIdentityProvider identityProvider,
        IPairingSessionManager pairingSessionManager,
        IDeviceIdentityKeyProvider keyProvider,
        ITrustedDeviceRepository trustedDevices,
        DeviceSessionRegistry registry,
        IncomingFileTransferManager incomingFiles,
        ITlsCertificateProvider certificateProvider,
        WindowsFileTransferTransport fileTransport,
        FeatureMessageTransport featureTransport,
        TransportSessionCoordinator sessionCoordinator,
        ILogger<BluetoothRfcommDeviceServer>? logger = null)
    {
        _identityProvider = identityProvider;
        _pairingSessionManager = pairingSessionManager;
        _keyProvider = keyProvider;
        _trustedDevices = trustedDevices;
        _registry = registry;
        _incomingFiles = incomingFiles;
        _certificateProvider = certificateProvider;
        _fileTransport = fileTransport;
        _featureTransport = featureTransport;
        _sessionCoordinator = sessionCoordinator;
        _logger = logger ?? NullLogger<BluetoothRfcommDeviceServer>.Instance;
    }

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public event EventHandler<DeviceSessionChangedEventArgs>? SessionChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;
        try
        {
            _listener = new BluetoothListener(ServiceId) { ServiceName = "DeviceSync secure fallback" };
            _listener.Start();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _acceptLoop = AcceptLoopAsync(_cts.Token);
            IsRunning = true;
            LastError = null;
            _logger.LogInformation("BLUETOOTH_RFCOMM_LISTENER_STARTED service={ServiceId}", ServiceId);
        }
        catch (Exception error)
        {
            LastError = error.Message;
            _logger.LogWarning(error, "BLUETOOTH_RFCOMM_UNAVAILABLE");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _listener?.Dispose();
        _cts?.Dispose();
        _listener = null;
        _cts = null;
        _acceptLoop = null;
        IsRunning = false;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            BluetoothClient client;
            try
            {
                client = await Task.Run(() => _listener!.AcceptBluetoothClient(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error)
            {
                if (!cancellationToken.IsCancellationRequested)
                    _logger.LogWarning(error, "BLUETOOTH_RFCOMM_ACCEPT_FAILED");
                break;
            }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(BluetoothClient client, CancellationToken cancellationToken)
    {
        try
        {
            var rawStream = client.GetStream();
            var sslStream = new SslStream(rawStream, leaveInnerStreamOpen: false);
            var certificate = await _certificateProvider.GetServerCertificateAsync(cancellationToken).ConfigureAwait(false);
            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);

            var endpoint = new DeviceTransportEndpoint(
                DeviceTransportKind.BluetoothRfcomm,
                client.RemoteMachineName ?? "paired-device");
            var session = new ClientSession(
                sslStream,
                () =>
                {
                    client.Dispose();
                    return ValueTask.CompletedTask;
                },
                endpoint,
                new ConnectionHandshakeHandler(_identityProvider),
                new AuthHandshakeHandler(
                    _identityProvider,
                    _keyProvider,
                    _trustedDevices,
                    pairingSessionManager: _pairingSessionManager),
                new PairingRequestHandler(_pairingSessionManager, _identityProvider, _keyProvider, _trustedDevices),
                new HeartbeatResponder(_identityProvider),
                _registry,
                _logger,
                _incomingFiles,
                _fileTransport,
                _featureTransport,
                sessionCoordinator: _sessionCoordinator);
            session.SessionChanged += (_, args) => SessionChanged?.Invoke(this, args);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AuthenticationException or IOException)
        {
            _logger.LogWarning(error, "BLUETOOTH_TLS_SESSION_FAILED");
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
