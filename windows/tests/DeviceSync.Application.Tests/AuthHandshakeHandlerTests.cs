using System.Security.Cryptography;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class AuthHandshakeHandlerTests
{
    [Fact]
    public async Task ActiveTrustedAndroid_GetsSignedChallenge_AndValidResponseIsAccepted()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        using var androidKey = new FakeDeviceIdentityKeyProvider();
        var identity = new FakeIdentityProvider("windows-test");
        var trusted = new FakeTrustedDeviceRepository();
        await trusted.SaveTrustedDeviceAsync(AndroidTrust(androidKey, TrustStatuses.Active));
        var handler = new AuthHandshakeHandler(identity, windowsKey, trusted);
        var hello = Hello(androidKey);

        var challenge = await handler.BuildChallengeAsync(hello);

        Assert.NotNull(challenge.Attempt);
        Assert.Equal(ProtocolMessageTypes.AuthChallenge, challenge.Response.Type);
        var challengePayload = ProtocolSerializer.DecodePayload<AuthChallengePayload>(challenge.Response.Payload);
        Assert.Equal(32, SecurityEncoding.Base64UrlDecode(challengePayload.ServerNonce).Length);
        Assert.True(windowsKey.Verify(
            await windowsKey.GetPublicKeyAsync(),
            challenge.Attempt!.Transcript,
            SecurityEncoding.Base64UrlDecode(challengePayload.ServerSignature)));

        var response = AuthResponse(challenge.Attempt, androidKey);
        var accepted = await handler.VerifyResponseAsync(challenge.Attempt, response);

        Assert.True(accepted.IsAccepted);
        Assert.Equal(ProtocolMessageTypes.AuthAccepted, accepted.Response.Type);
        Assert.NotNull((await trusted.GetTrustedDeviceAsync("android-test"))?.LastVerifiedAtUtc);

        var replay = await handler.VerifyResponseAsync(challenge.Attempt, response);
        Assert.False(replay.IsAccepted);
        Assert.Equal(ProtocolMessageTypes.AuthRejected, replay.Response.Type);
    }

    [Fact]
    public async Task UnknownAndroid_GetsPairingRequired()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        using var androidKey = new FakeDeviceIdentityKeyProvider();
        var handler = new AuthHandshakeHandler(new FakeIdentityProvider("windows-test"), windowsKey, new FakeTrustedDeviceRepository());

        var result = await handler.BuildChallengeAsync(Hello(androidKey));

        Assert.Null(result.Attempt);
        var payload = ProtocolSerializer.DecodePayload<ProtocolErrorPayload>(result.Response.Payload);
        Assert.Equal("PAIRING_REQUIRED", payload.Code);
    }

    [Fact]
    public async Task ChangedFingerprint_GetsIdentityKeyChanged()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        using var androidKey = new FakeDeviceIdentityKeyProvider();
        using var otherAndroidKey = new FakeDeviceIdentityKeyProvider();
        var trusted = new FakeTrustedDeviceRepository();
        await trusted.SaveTrustedDeviceAsync(AndroidTrust(otherAndroidKey, TrustStatuses.Active));
        var handler = new AuthHandshakeHandler(new FakeIdentityProvider("windows-test"), windowsKey, trusted);

        var result = await handler.BuildChallengeAsync(Hello(androidKey));

        var payload = ProtocolSerializer.DecodePayload<ProtocolErrorPayload>(result.Response.Payload);
        Assert.Equal("IDENTITY_KEY_CHANGED", payload.Code);
    }

    [Fact]
    public async Task RevokedAndroid_GetsTrustRevoked()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        using var androidKey = new FakeDeviceIdentityKeyProvider();
        var trusted = new FakeTrustedDeviceRepository();
        await trusted.SaveTrustedDeviceAsync(AndroidTrust(androidKey, TrustStatuses.Revoked) with { RevokedAtUtc = DateTimeOffset.UtcNow });
        var handler = new AuthHandshakeHandler(new FakeIdentityProvider("windows-test"), windowsKey, trusted);

        var result = await handler.BuildChallengeAsync(Hello(androidKey));

        var payload = ProtocolSerializer.DecodePayload<ProtocolErrorPayload>(result.Response.Payload);
        Assert.Equal("TRUST_REVOKED", payload.Code);
    }

    [Fact]
    public async Task MismatchHelloMessageId_IsRejected()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        using var androidKey = new FakeDeviceIdentityKeyProvider();
        var trusted = new FakeTrustedDeviceRepository();
        await trusted.SaveTrustedDeviceAsync(AndroidTrust(androidKey, TrustStatuses.Active));
        var handler = new AuthHandshakeHandler(new FakeIdentityProvider("windows-test"), windowsKey, trusted);
        var challenge = await handler.BuildChallengeAsync(Hello(androidKey));
        var badResponse = new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = "auth-response-1",
            Type = ProtocolMessageTypes.AuthResponse,
            SenderDeviceId = "android-test",
            RecipientDeviceId = "windows-test",
            TimestampUtc = "2026-07-06T12:00:02Z",
            Payload = ProtocolSerializer.PayloadToJson(new AuthResponsePayload
            {
                HelloMessageId = "old-hello",
                ClientSignature = SecurityEncoding.Base64UrlEncode(await androidKey.SignAsync(challenge.Attempt!.Transcript)),
            }),
        };

        var result = await handler.VerifyResponseAsync(challenge.Attempt!, badResponse);

        Assert.False(result.IsAccepted);
        Assert.Equal(ProtocolMessageTypes.AuthRejected, result.Response.Type);
    }

    private static ProtocolMessage Hello(FakeDeviceIdentityKeyProvider androidKey) => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "hello-1",
        Type = ProtocolMessageTypes.ConnectionHello,
        SenderDeviceId = "android-test",
        TimestampUtc = "2026-07-06T12:00:00Z",
        Payload = ProtocolSerializer.PayloadToJson(new ConnectionHelloPayload
        {
            DeviceName = "Pixel",
            AppVersion = "1.0",
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
            IdentityFingerprint = androidKey.GetPublicKeyFingerprintAsync().GetAwaiter().GetResult(),
            ClientNonce = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            AuthVersion = 1,
        }),
    };

    private static ProtocolMessage AuthResponse(AuthAttempt attempt, FakeDeviceIdentityKeyProvider androidKey) => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "auth-response-1",
        Type = ProtocolMessageTypes.AuthResponse,
        SenderDeviceId = "android-test",
        RecipientDeviceId = "windows-test",
        TimestampUtc = "2026-07-06T12:00:02Z",
        Payload = ProtocolSerializer.PayloadToJson(new AuthResponsePayload
        {
            HelloMessageId = attempt.HelloMessageId,
            ClientSignature = SecurityEncoding.Base64UrlEncode(androidKey.SignAsync(attempt.Transcript).GetAwaiter().GetResult()),
        }),
    };

    private static TrustedDevice AndroidTrust(FakeDeviceIdentityKeyProvider androidKey, string status) => new()
    {
        DeviceId = "android-test",
        DeviceName = "Pixel",
        IdentityPublicKey = SecurityEncoding.Base64UrlEncode(androidKey.GetPublicKeyAsync().GetAwaiter().GetResult()),
        IdentityFingerprint = androidKey.GetPublicKeyFingerprintAsync().GetAwaiter().GetResult(),
        FutureTlsCertificateFingerprint = null,
        PairedAtUtc = DateTimeOffset.UtcNow,
        LastVerifiedAtUtc = null,
        RevokedAtUtc = null,
        TrustStatus = status,
    };
}
