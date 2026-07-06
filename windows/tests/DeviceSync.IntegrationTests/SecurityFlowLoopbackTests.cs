using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.IntegrationTests;

public sealed class SecurityFlowLoopbackTests
{
    [Fact]
    public async Task PairingAuthHeartbeatAndWindowsRevoke_EndToEnd()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            var qr = await fixture.Pairing.StartPairingAsync(fixture.Server.Port, ["127.0.0.1"]);
            var parsedQr = JsonSerializer.Deserialize<PairingQrPayload>(
                JsonSerializer.Serialize(qr, JsonOptions),
                JsonOptions)!;

            await android.PairAsync(parsedQr, fixture.Pairing);
            var trustedAndroid = await fixture.Trust.GetTrustedDeviceAsync(android.DeviceId);
            Assert.Equal(TrustStatuses.Active, trustedAndroid?.TrustStatus);
            Assert.Null(fixture.Pairing.CurrentSession);

            await android.AuthenticateAsync(fixture.Server.Port, fixture.Registry);
            Assert.Equal(android.DeviceId, fixture.Registry.ActiveSession?.DeviceId);
            await android.PingAsync();

            await fixture.Trust.RevokeAsync(android.DeviceId, DateTimeOffset.UtcNow);
            await fixture.Server.DisconnectActiveSessionAsync();
            await WaitUntilAsync(() => fixture.Registry.ActiveSession is null);

            var rejected = await android.TryAuthenticateExpectingErrorAsync(fixture.Server.Port);
            Assert.Equal("TRUST_REVOKED", rejected);
            Assert.Null(fixture.Registry.ActiveSession);
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    [Fact]
    public async Task ChangedAndroidIdentityKey_IsRejected()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");
        using var changedAndroid = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            var qr = await fixture.Pairing.StartPairingAsync(fixture.Server.Port, ["127.0.0.1"]);
            await android.PairAsync(qr, fixture.Pairing);

            var error = await changedAndroid.TryAuthenticateExpectingErrorAsync(fixture.Server.Port);

            Assert.Equal("IDENTITY_KEY_CHANGED", error);
            Assert.Null(fixture.Registry.ActiveSession);
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    [Fact]
    public async Task ConsumedPairingSession_RejectsRepeatedPairingRequest()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            var qr = await fixture.Pairing.StartPairingAsync(fixture.Server.Port, ["127.0.0.1"]);
            await android.PairAsync(qr, fixture.Pairing);
            await WaitUntilAsync(() => fixture.Pairing.CurrentSession is null);

            await using var client = await android.ConnectAsync(fixture.Server.Port);
            await client.Writer.WriteAsync(android.BuildPairingRequest(qr));
            var rejected = await client.Reader.ReadAsync();

            Assert.Equal(ProtocolMessageTypes.PairingRejected, rejected.Type);
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    [Fact]
    public async Task WrongPairingHmac_DoesNotCreateTrust()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            var qr = await fixture.Pairing.StartPairingAsync(fixture.Server.Port, ["127.0.0.1"]);
            await using var client = await android.ConnectAsync(fixture.Server.Port);
            var request = android.BuildPairingRequest(qr, proofOverride: SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
            await client.Writer.WriteAsync(request);
            var rejected = await client.Reader.ReadAsync();

            Assert.Equal(ProtocolMessageTypes.PairingRejected, rejected.Type);
            Assert.Null(await fixture.Trust.GetTrustedDeviceAsync(android.DeviceId));
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    [Fact]
    public async Task PendingTrust_IsNotAcceptedForAuth()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            await fixture.Trust.SaveTrustedDeviceAsync(android.ToTrustedDevice(TrustStatuses.Pending));

            var error = await android.TryAuthenticateExpectingErrorAsync(fixture.Server.Port);

            Assert.Equal("PAIRING_REQUIRED", error);
            Assert.Null(fixture.Registry.ActiveSession);
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    [Fact]
    public async Task InvalidAuthSignature_DoesNotCreateActiveSession()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            var qr = await fixture.Pairing.StartPairingAsync(fixture.Server.Port, ["127.0.0.1"]);
            await android.PairAsync(qr, fixture.Pairing);

            var rejected = await android.AuthenticateWithInvalidSignatureAsync(fixture.Server.Port, fixture.Registry);

            Assert.Equal(ProtocolMessageTypes.AuthRejected, rejected.Type);
            Assert.Null(fixture.Registry.ActiveSession);
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    [Fact]
    public async Task RepeatedAuthResponse_IsRejected()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            var qr = await fixture.Pairing.StartPairingAsync(fixture.Server.Port, ["127.0.0.1"]);
            await android.PairAsync(qr, fixture.Pairing);

            var rejected = await android.SendRepeatedAuthResponseAsync(fixture.Server.Port, fixture.Registry);

            Assert.Equal(ProtocolMessageTypes.ProtocolError, rejected.Type);
            Assert.Equal("AUTH_ALREADY_ACCEPTED", ProtocolSerializer.DecodePayload<ProtocolErrorPayload>(rejected.Payload).Code);
            Assert.Equal(android.DeviceId, fixture.Registry.ActiveSession?.DeviceId);
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    [Fact]
    public async Task HeartbeatBeforeAuthAccepted_IsRejectedAndDoesNotCreateActiveSession()
    {
        using var workspace = TempWorkspace.Create();
        var fixture = await SecurityFixture.StartAsync(workspace);
        using var android = AndroidPeer.Create("android-loopback", "Pixel");

        try
        {
            var qr = await fixture.Pairing.StartPairingAsync(fixture.Server.Port, ["127.0.0.1"]);
            await android.PairAsync(qr, fixture.Pairing);

            var rejected = await android.SendHeartbeatBeforeAuthAcceptedAsync(fixture.Server.Port);

            Assert.Equal(ProtocolMessageTypes.AuthRejected, rejected.Type);
            Assert.Null(fixture.Registry.ActiveSession);
        }
        finally
        {
            await fixture.Server.StopAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(20, cts.Token);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class SecurityFixture
    {
        private SecurityFixture(
            TcpDeviceServer server,
            PairingSessionManager pairing,
            FileTrustedDeviceRepository trust,
            DeviceSessionRegistry registry)
        {
            Server = server;
            Pairing = pairing;
            Trust = trust;
            Registry = registry;
        }

        public TcpDeviceServer Server { get; }
        public PairingSessionManager Pairing { get; }
        public FileTrustedDeviceRepository Trust { get; }
        public DeviceSessionRegistry Registry { get; }

        public static async Task<SecurityFixture> StartAsync(TempWorkspace workspace)
        {
            var port = GetFreePort();
            var identity = new IntegrationIdentityProvider("windows-loopback", port);
            var keyProvider = new EphemeralDeviceIdentityKeyProvider();
            var pairing = new PairingSessionManager(identity, keyProvider);
            var trust = new FileTrustedDeviceRepository(Path.Combine(workspace.Path, "trusted-devices.json"));
            var registry = new DeviceSessionRegistry();
            var server = new TcpDeviceServer(
                identity,
                registry,
                pairingSessionManager: pairing,
                keyProvider: keyProvider,
                trustedDeviceRepository: trust);
            await server.StartAsync();
            return new SecurityFixture(server, pairing, trust, registry);
        }
    }

    private sealed class AndroidPeer : IDisposable
    {
        private readonly ECDsa _key;
        private string? _windowsDeviceId;
        private string? _windowsDeviceName;
        private string? _windowsPublicKey;
        private string? _windowsFingerprint;
        private ProtocolFrameReader? _activeReader;
        private ProtocolFrameWriter? _activeWriter;
        private TcpClient? _activeClient;

        private AndroidPeer(string deviceId, string deviceName, ECDsa key)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            _key = key;
        }

        public string DeviceId { get; }
        public string DeviceName { get; }
        public string PublicKey => SecurityEncoding.Base64UrlEncode(_key.ExportSubjectPublicKeyInfo());
        public string Fingerprint => SecurityEncoding.Fingerprint(_key.ExportSubjectPublicKeyInfo());

        public static AndroidPeer Create(string deviceId, string deviceName)
        {
            return new AndroidPeer(deviceId, deviceName, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        }

        public async Task<PairingQrPayload> PairAsync(PairingQrPayload qr, IPairingSessionManager pairing)
        {
            await using var client = await ConnectAsync(qr.Port);
            var request = BuildPairingRequest(qr);
            await client.Writer.WriteAsync(request);
            var challengeMessage = await client.Reader.ReadAsync();
            Assert.Equal(ProtocolMessageTypes.PairingChallenge, challengeMessage.Type);
            var challenge = ProtocolSerializer.DecodePayload<PairingChallengePayload>(challengeMessage.Payload);

            VerifyChallenge(qr, challenge);
            var code = VerificationCode(qr, challenge);
            pairing.ConfirmLocalUser();
            await client.Writer.WriteAsync(BuildConfirm(qr, challenge, code));

            var acceptedMessage = await client.Reader.ReadAsync();
            Assert.Equal(ProtocolMessageTypes.PairingAccepted, acceptedMessage.Type);
            var accepted = ProtocolSerializer.DecodePayload<PairingAcceptedPayload>(acceptedMessage.Payload);
            VerifyAccepted(qr, challenge, code, accepted);

            _windowsDeviceId = qr.WindowsDeviceId;
            _windowsDeviceName = qr.WindowsDeviceName;
            _windowsPublicKey = qr.WindowsIdentityPublicKey;
            _windowsFingerprint = qr.WindowsIdentityFingerprint;

            await client.Writer.WriteAsync(new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = ProtocolMessageTypes.PairingCompleteAck,
                SenderDeviceId = DeviceId,
                RecipientDeviceId = qr.WindowsDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(new PairingCompleteAckPayload
                {
                    SessionId = qr.SessionId,
                    Status = "stored",
                }),
            });

            return qr;
        }

        public async Task AuthenticateAsync(int port, DeviceSessionRegistry registry)
        {
            var client = await ConnectAsync(port);
            _activeClient = client.Client;
            _activeReader = client.Reader;
            _activeWriter = client.Writer;
            var hello = BuildHello();
            await _activeWriter.WriteAsync(hello);
            var challenge = await _activeReader.ReadAsync();
            Assert.Null(registry.ActiveSession);
            var auth = VerifyAuthChallenge(hello, challenge);
            await _activeWriter.WriteAsync(BuildAuthResponse(auth, hello.MessageId));
            var accepted = await _activeReader.ReadAsync();
            Assert.Equal(ProtocolMessageTypes.AuthAccepted, accepted.Type);
            await WaitUntilAsync(() => registry.ActiveSession?.DeviceId == DeviceId);
        }

        public async Task<string> TryAuthenticateExpectingErrorAsync(int port)
        {
            await using var client = await ConnectAsync(port);
            await client.Writer.WriteAsync(BuildHello());
            var error = await client.Reader.ReadAsync();
            Assert.Equal(ProtocolMessageTypes.ProtocolError, error.Type);
            return ProtocolSerializer.DecodePayload<ProtocolErrorPayload>(error.Payload).Code;
        }

        public async Task PingAsync()
        {
            Assert.NotNull(_activeWriter);
            Assert.NotNull(_activeReader);
            var ping = new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = "ping-loopback",
                Type = ProtocolMessageTypes.ConnectionPing,
                SenderDeviceId = DeviceId,
                RecipientDeviceId = _windowsDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(new PingPayload
                {
                    Sequence = 1,
                    SentAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                }),
            };
            await _activeWriter!.WriteAsync(ping);
            var pong = await _activeReader!.ReadAsync();
            Assert.Equal(ProtocolMessageTypes.ConnectionPong, pong.Type);
            Assert.Equal(ping.MessageId, pong.CorrelationId);
        }

        public async Task<LoopbackConnection> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            return new LoopbackConnection(tcp, new ProtocolFrameReader(stream), new ProtocolFrameWriter(stream));
        }

        public ProtocolMessage BuildPairingRequest(PairingQrPayload qr, string? proofOverride = null)
        {
            var androidNonce = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var transcript = TranscriptBuilder.PairingRequest(
                qr.SessionId,
                qr.WindowsDeviceId,
                DeviceId,
                qr.WindowsIdentityFingerprint,
                Fingerprint,
                androidNonce);
            var proof = proofOverride ?? SecurityEncoding.Base64UrlEncode(SecurityEncoding.HmacSha256(SecurityEncoding.Base64UrlDecode(qr.PairingSecret), transcript));
            return new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = ProtocolMessageTypes.PairingRequest,
                SenderDeviceId = DeviceId,
                RecipientDeviceId = qr.WindowsDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(new PairingRequestPayload
                {
                    SessionId = qr.SessionId,
                    AndroidDeviceId = DeviceId,
                    AndroidDeviceName = DeviceName,
                    AndroidAppVersion = "test",
                    AndroidIdentityPublicKey = PublicKey,
                    AndroidIdentityFingerprint = Fingerprint,
                    AndroidNonce = androidNonce,
                    Proof = proof,
                }),
            };
        }

        private ProtocolMessage BuildConfirm(PairingQrPayload qr, PairingChallengePayload challenge, string code)
        {
            var transcript = TranscriptBuilder.PairingConfirmation(
                qr.SessionId,
                qr.WindowsDeviceId,
                DeviceId,
                qr.WindowsIdentityFingerprint,
                Fingerprint,
                challenge.AndroidNonce,
                challenge.WindowsNonce,
                code);
            return new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = ProtocolMessageTypes.PairingConfirm,
                SenderDeviceId = DeviceId,
                RecipientDeviceId = qr.WindowsDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(new PairingConfirmPayload
                {
                    SessionId = qr.SessionId,
                    Confirmed = true,
                    AndroidSignature = SecurityEncoding.Base64UrlEncode(Sign(transcript)),
                }),
            };
        }

        private ProtocolMessage BuildHello()
        {
            var nonce = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            return new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = ProtocolMessageTypes.ConnectionHello,
                SenderDeviceId = DeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(new ConnectionHelloPayload
                {
                    DeviceName = DeviceName,
                    AppVersion = "test",
                    ProtocolVersion = ProtocolConstants.ProtocolVersion,
                    Capabilities = SupportedCapabilities.Values,
                    IdentityFingerprint = Fingerprint,
                    ClientNonce = nonce,
                    AuthVersion = 1,
                }),
            };
        }

        private AuthResponsePayload VerifyAuthChallenge(ProtocolMessage hello, ProtocolMessage challengeMessage)
        {
            Assert.Equal(ProtocolMessageTypes.AuthChallenge, challengeMessage.Type);
            Assert.Equal(hello.MessageId, challengeMessage.CorrelationId);
            var challenge = ProtocolSerializer.DecodePayload<AuthChallengePayload>(challengeMessage.Payload);
            Assert.Equal(hello.MessageId, challenge.HelloMessageId);
            Assert.Equal(32, SecurityEncoding.Base64UrlDecode(challenge.ServerNonce).Length);
            Assert.Equal(_windowsFingerprint, challenge.WindowsIdentityFingerprint);
            var helloPayload = ProtocolSerializer.DecodePayload<ConnectionHelloPayload>(hello.Payload);
            var transcript = TranscriptBuilder.SessionAuth(
                ProtocolConstants.ProtocolVersion,
                DeviceId,
                _windowsDeviceId!,
                Fingerprint,
                _windowsFingerprint!,
                helloPayload.ClientNonce!,
                challenge.ServerNonce,
                hello.MessageId);
            Assert.True(Verify(_windowsPublicKey!, transcript, challenge.ServerSignature));
            return new AuthResponsePayload
            {
                HelloMessageId = hello.MessageId,
                ClientSignature = SecurityEncoding.Base64UrlEncode(Sign(transcript)),
            };
        }

        private ProtocolMessage BuildAuthResponse(AuthResponsePayload response, string helloMessageId)
        {
            return new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = ProtocolMessageTypes.AuthResponse,
                SenderDeviceId = DeviceId,
                RecipientDeviceId = _windowsDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(response),
            };
        }

        public async Task<ProtocolMessage> AuthenticateWithInvalidSignatureAsync(int port, DeviceSessionRegistry registry)
        {
            await using var client = await ConnectAsync(port);
            var hello = BuildHello();
            await client.Writer.WriteAsync(hello);
            var challenge = await client.Reader.ReadAsync();
            Assert.Null(registry.ActiveSession);
            var auth = VerifyAuthChallenge(hello, challenge);
            auth = auth with { ClientSignature = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(64)) };
            await client.Writer.WriteAsync(BuildAuthResponse(auth, hello.MessageId));
            return await client.Reader.ReadAsync();
        }

        public async Task<ProtocolMessage> SendRepeatedAuthResponseAsync(int port, DeviceSessionRegistry registry)
        {
            await using var client = await ConnectAsync(port);
            var hello = BuildHello();
            await client.Writer.WriteAsync(hello);
            var challenge = await client.Reader.ReadAsync();
            Assert.Null(registry.ActiveSession);
            var auth = VerifyAuthChallenge(hello, challenge);
            var response = BuildAuthResponse(auth, hello.MessageId);
            await client.Writer.WriteAsync(response);
            var accepted = await client.Reader.ReadAsync();
            Assert.Equal(ProtocolMessageTypes.AuthAccepted, accepted.Type);
            await WaitUntilAsync(() => registry.ActiveSession?.DeviceId == DeviceId);

            await client.Writer.WriteAsync(response);
            return await client.Reader.ReadAsync();
        }

        public async Task<ProtocolMessage> SendHeartbeatBeforeAuthAcceptedAsync(int port)
        {
            await using var client = await ConnectAsync(port);
            var hello = BuildHello();
            await client.Writer.WriteAsync(hello);
            var challenge = await client.Reader.ReadAsync();
            VerifyAuthChallenge(hello, challenge);
            await client.Writer.WriteAsync(new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = ProtocolMessageTypes.ConnectionPing,
                SenderDeviceId = DeviceId,
                RecipientDeviceId = _windowsDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(new PingPayload
                {
                    Sequence = 1,
                    SentAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                }),
            });
            return await client.Reader.ReadAsync();
        }

        public TrustedDevice ToTrustedDevice(string status)
        {
            return new TrustedDevice
            {
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                IdentityPublicKey = PublicKey,
                IdentityFingerprint = Fingerprint,
                FutureTlsCertificateFingerprint = null,
                PairedAtUtc = DateTimeOffset.UtcNow,
                LastVerifiedAtUtc = null,
                RevokedAtUtc = status == TrustStatuses.Revoked ? DateTimeOffset.UtcNow : null,
                TrustStatus = status,
            };
        }

        private void VerifyChallenge(PairingQrPayload qr, PairingChallengePayload challenge)
        {
            Assert.Equal(qr.SessionId, challenge.SessionId);
            Assert.Equal(qr.WindowsDeviceId, challenge.WindowsDeviceId);
            Assert.Equal(qr.WindowsIdentityFingerprint, challenge.WindowsIdentityFingerprint);
            Assert.Equal(32, SecurityEncoding.Base64UrlDecode(challenge.WindowsNonce).Length);
            var expected = SecurityEncoding.HmacSha256(
                SecurityEncoding.Base64UrlDecode(qr.PairingSecret),
                TranscriptBuilder.PairingChallenge(
                    qr.SessionId,
                    qr.WindowsDeviceId,
                    DeviceId,
                    qr.WindowsIdentityFingerprint,
                    Fingerprint,
                    challenge.AndroidNonce,
                    challenge.WindowsNonce));
            Assert.True(SecurityEncoding.FixedTimeEquals(expected, SecurityEncoding.Base64UrlDecode(challenge.Proof)));
        }

        private string VerificationCode(PairingQrPayload qr, PairingChallengePayload challenge)
        {
            return SecurityEncoding.VerificationCode(
                qr.SessionId,
                qr.WindowsDeviceId,
                DeviceId,
                qr.WindowsIdentityFingerprint,
                Fingerprint,
                challenge.AndroidNonce,
                challenge.WindowsNonce);
        }

        private void VerifyAccepted(PairingQrPayload qr, PairingChallengePayload challenge, string code, PairingAcceptedPayload accepted)
        {
            Assert.Equal(qr.SessionId, accepted.SessionId);
            Assert.Equal(new[] { "basic_connection", "heartbeat" }, accepted.Permissions);
            var transcript = TranscriptBuilder.PairingAccepted(
                qr.SessionId,
                qr.WindowsDeviceId,
                DeviceId,
                qr.WindowsIdentityFingerprint,
                Fingerprint,
                challenge.AndroidNonce,
                challenge.WindowsNonce,
                code,
                accepted.PairedAtUtc,
                accepted.Permissions);
            Assert.True(Verify(qr.WindowsIdentityPublicKey, transcript, accepted.WindowsSignature));
        }

        private byte[] Sign(byte[] transcript)
        {
            return _key.SignData(transcript, HashAlgorithmName.SHA256);
        }

        private static bool Verify(string publicKey, byte[] transcript, string signature)
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(SecurityEncoding.Base64UrlDecode(publicKey), out _);
            return key.VerifyData(transcript, SecurityEncoding.Base64UrlDecode(signature), HashAlgorithmName.SHA256);
        }

        public void Dispose()
        {
            _activeClient?.Dispose();
            _key.Dispose();
        }
    }

    private sealed record LoopbackConnection(TcpClient Client, ProtocolFrameReader Reader, ProtocolFrameWriter Writer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EphemeralDeviceIdentityKeyProvider : IDeviceIdentityKeyProvider, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_key.ExportSubjectPublicKeyInfo());
        }

        public async Task<string> GetPublicKeyFingerprintAsync(CancellationToken cancellationToken = default)
        {
            return SecurityEncoding.Fingerprint(await GetPublicKeyAsync(cancellationToken));
        }

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_key.SignData(data.Span, HashAlgorithmName.SHA256));
        }

        public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }

        public void Dispose()
        {
            _key.Dispose();
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"devicesync-security-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
