using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.IntegrationTests;

public sealed class TcpDeviceServerIntegrationTests
{
    [Fact]
    public async Task AndroidClient_CanHandshakePingClose_AndServerKeepsListening()
    {
        var port = GetFreePort();
        var identity = new IntegrationIdentityProvider("windows-integration", port);
        var server = new TcpDeviceServer(identity, new DeviceSessionRegistry());

        await server.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = client.GetStream();
            var reader = new ProtocolFrameReader(stream);
            var writer = new ProtocolFrameWriter(stream);

            await writer.WriteAsync(AndroidHello());
            var ack = await reader.ReadAsync();
            Assert.Equal(ProtocolMessageTypes.ConnectionHelloAck, ack.Type);
            Assert.Equal("hello-1", ack.CorrelationId);

            await writer.WriteAsync(AndroidPing());
            var pong = await reader.ReadAsync();
            var pongPayload = ProtocolSerializer.DecodePayload<PongPayload>(pong.Payload);
            Assert.Equal(ProtocolMessageTypes.ConnectionPong, pong.Type);
            Assert.Equal(99, pongPayload.Sequence);
            Assert.Equal("ping-1", pong.CorrelationId);

            await writer.WriteAsync(AndroidClose());
            await Task.Delay(200);
            Assert.True(server.IsRunning);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Listener_BindsToAnyIPv4Endpoint()
    {
        var port = GetFreePort();
        var identity = new IntegrationIdentityProvider("windows-integration", port);
        var server = new TcpDeviceServer(identity, new DeviceSessionRegistry());

        await server.StartAsync();
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();

            Assert.Contains(listeners, endpoint =>
                endpoint.Port == port && endpoint.Address.Equals(IPAddress.Any));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task TlsServer_AcceptsPinnedIdentity_AndRejectsWrongPin()
    {
        var port = GetFreePort();
        var identity = new IntegrationIdentityProvider("windows-tls", port);
        var certificates = new WindowsDeviceIdentityKeyProvider(
            new MemoryTlsKeyStorage(),
            new PassThroughTlsProtector());
        await using var server = new TcpDeviceServer(
            identity,
            new DeviceSessionRegistry(),
            tlsCertificateProvider: certificates);
        await server.StartAsync();

        try
        {
            var expectedPin = await certificates.GetServerSpkiFingerprintAsync();
            System.Security.Cryptography.X509Certificates.X509Certificate? presentedCertificate = null;
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var tls = new SslStream(
                client.GetStream(),
                false,
                (_, certificate, _, _) =>
                {
                    presentedCertificate = certificate;
                    return certificate is not null;
                });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "DeviceSync",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            });
            Assert.NotNull(presentedCertificate);
            Assert.True(CertificateMatchesPin(presentedCertificate!, expectedPin));

            var reader = new ProtocolFrameReader(tls);
            var writer = new ProtocolFrameWriter(tls);
            await writer.WriteAsync(AndroidHello());
            Assert.Equal(ProtocolMessageTypes.ConnectionHelloAck, (await reader.ReadAsync()).Type);

            using var wrongPinClient = new TcpClient();
            await wrongPinClient.ConnectAsync(IPAddress.Loopback, port);
            await using var wrongPinTls = new SslStream(wrongPinClient.GetStream(), false, (_, _, _, _) => false);
            await Assert.ThrowsAnyAsync<AuthenticationException>(() => wrongPinTls.AuthenticateAsClientAsync("DeviceSync"));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static bool CertificateMatchesPin(System.Security.Cryptography.X509Certificates.X509Certificate certificate, string expectedPin)
    {
        using var certificate2 = new X509Certificate2(certificate);
        using var key = certificate2.GetECDsaPublicKey();
        return key is not null && SecurityEncoding.Fingerprint(key.ExportSubjectPublicKeyInfo()) == expectedPin;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ProtocolMessage AndroidHello() => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "hello-1",
        Type = ProtocolMessageTypes.ConnectionHello,
        SenderDeviceId = "android-integration",
        TimestampUtc = "2026-07-05T18:45:00Z",
        Payload = ProtocolSerializer.PayloadToJson(new ConnectionHelloPayload
        {
            DeviceName = "Pixel",
            AppVersion = "1.0",
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
        }),
    };

    private static ProtocolMessage AndroidPing() => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "ping-1",
        Type = ProtocolMessageTypes.ConnectionPing,
        SenderDeviceId = "android-integration",
        RecipientDeviceId = "windows-integration",
        TimestampUtc = "2026-07-05T18:45:01Z",
        Payload = ProtocolSerializer.PayloadToJson(new PingPayload
        {
            Sequence = 99,
            SentAtUtc = "2026-07-05T18:45:01Z",
        }),
    };

    private static ProtocolMessage AndroidClose() => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "close-1",
        Type = ProtocolMessageTypes.ConnectionClose,
        SenderDeviceId = "android-integration",
        RecipientDeviceId = "windows-integration",
        TimestampUtc = "2026-07-05T18:45:02Z",
        Payload = ProtocolSerializer.PayloadToJson(new ConnectionClosePayload { Reason = "test", AllowReconnect = true }),
    };
}

internal sealed class MemoryTlsKeyStorage : IProtectedKeyStorage
{
    private byte[]? _bytes;
    public Task<byte[]?> ReadProtectedAsync(CancellationToken cancellationToken = default) => Task.FromResult(_bytes);
    public Task WriteProtectedAtomicAsync(byte[] protectedBytes, CancellationToken cancellationToken = default)
    {
        _bytes = protectedBytes.ToArray();
        return Task.CompletedTask;
    }
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        _bytes = null;
        return Task.CompletedTask;
    }
}

internal sealed class PassThroughTlsProtector : IDataProtector
{
    public byte[] Protect(byte[] plainBytes) => plainBytes.ToArray();
    public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
}


internal sealed class IntegrationIdentityProvider : IWindowsDeviceIdentityProvider
{
    private AppSettings _settings;

    public IntegrationIdentityProvider(string deviceId, int port)
    {
        _settings = new AppSettings { WindowsDeviceId = deviceId, Port = port };
    }

    public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_settings.WindowsDeviceId!);
    }

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_settings);
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}
