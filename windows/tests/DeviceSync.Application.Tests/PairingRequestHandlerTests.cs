using System.Security.Cryptography;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class PairingRequestHandlerTests
{
    [Fact]
    public async Task ValidPairingRequest_ReturnsChallenge()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        var identity = new FakeIdentityProvider("windows-test");
        var manager = new PairingSessionManager(identity, windowsKey);
        var qr = await manager.StartPairingAsync(54321, ["127.0.0.1"]);
        using var androidKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var androidPublicKey = androidKey.ExportSubjectPublicKeyInfo();
        var androidFingerprint = SecurityEncoding.Fingerprint(androidPublicKey);
        var androidNonce = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var transcript = TranscriptBuilder.PairingRequest(
            qr.SessionId,
            qr.WindowsDeviceId,
            "android-test",
            qr.WindowsIdentityFingerprint,
            androidFingerprint,
            androidNonce);
        var proof = SecurityEncoding.Base64UrlEncode(SecurityEncoding.HmacSha256(manager.CurrentSession!.PairingSecret, transcript));

        var handler = new PairingRequestHandler(manager, identity, windowsKey, new FakeTrustedDeviceRepository());
        var result = await handler.HandleAsync(Request(qr.SessionId, androidPublicKey, androidFingerprint, androidNonce, proof));

        Assert.True(result.Accepted);
        Assert.Equal(ProtocolMessageTypes.PairingChallenge, result.Response.Type);
        var challenge = ProtocolSerializer.DecodePayload<PairingChallengePayload>(result.Response.Payload);
        Assert.Equal(androidNonce, challenge.AndroidNonce);
        Assert.Equal(qr.WindowsIdentityFingerprint, challenge.WindowsIdentityFingerprint);
        Assert.Equal(32, SecurityEncoding.Base64UrlDecode(challenge.WindowsNonce).Length);
    }

    [Fact]
    public async Task WrongProof_ReturnsGenericPairingRejected()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        var identity = new FakeIdentityProvider("windows-test");
        var manager = new PairingSessionManager(identity, windowsKey);
        var qr = await manager.StartPairingAsync(54321, ["127.0.0.1"]);
        using var androidKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var androidPublicKey = androidKey.ExportSubjectPublicKeyInfo();
        var androidFingerprint = SecurityEncoding.Fingerprint(androidPublicKey);

        var handler = new PairingRequestHandler(manager, identity, windowsKey, new FakeTrustedDeviceRepository());
        var result = await handler.HandleAsync(Request(
            qr.SessionId,
            androidPublicKey,
            androidFingerprint,
            SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))));

        Assert.False(result.Accepted);
        Assert.Equal(ProtocolMessageTypes.PairingRejected, result.Response.Type);
    }

    [Fact]
    public async Task ValidConfirm_CreatesPendingTrust_AndCompleteAckActivatesIt()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        var identity = new FakeIdentityProvider("windows-test");
        var manager = new PairingSessionManager(identity, windowsKey);
        var trusted = new FakeTrustedDeviceRepository();
        var qr = await manager.StartPairingAsync(54321, ["127.0.0.1"]);
        using var androidKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var androidPublicKey = androidKey.ExportSubjectPublicKeyInfo();
        var androidFingerprint = SecurityEncoding.Fingerprint(androidPublicKey);
        var androidNonce = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var proofTranscript = TranscriptBuilder.PairingRequest(
            qr.SessionId,
            qr.WindowsDeviceId,
            "android-test",
            qr.WindowsIdentityFingerprint,
            androidFingerprint,
            androidNonce);
        var proof = SecurityEncoding.Base64UrlEncode(SecurityEncoding.HmacSha256(manager.CurrentSession!.PairingSecret, proofTranscript));
        var handler = new PairingRequestHandler(manager, identity, windowsKey, trusted);

        await handler.HandleAsync(Request(qr.SessionId, androidPublicKey, androidFingerprint, androidNonce, proof));
        manager.ConfirmLocalUser();
        var session = manager.CurrentSession!;
        var confirmTranscript = TranscriptBuilder.PairingConfirmation(
            qr.SessionId,
            qr.WindowsDeviceId,
            "android-test",
            qr.WindowsIdentityFingerprint,
            androidFingerprint,
            androidNonce,
            SecurityEncoding.Base64UrlEncode(session.WindowsNonce),
            session.VerificationCode!);
        var accepted = await handler.HandleConfirmAsync(Confirm(qr.SessionId, androidKey.SignData(confirmTranscript, HashAlgorithmName.SHA256)));

        Assert.Equal(ProtocolMessageTypes.PairingAccepted, accepted.Type);
        var pending = await trusted.GetTrustedDeviceAsync("android-test");
        Assert.Equal(TrustStatuses.Pending, pending?.TrustStatus);
        var acceptedPayload = ProtocolSerializer.DecodePayload<PairingAcceptedPayload>(accepted.Payload);
        var acceptedTranscript = TranscriptBuilder.PairingAccepted(
            qr.SessionId,
            qr.WindowsDeviceId,
            "android-test",
            qr.WindowsIdentityFingerprint,
            androidFingerprint,
            androidNonce,
            SecurityEncoding.Base64UrlEncode(session.WindowsNonce),
            session.VerificationCode!,
            acceptedPayload.PairedAtUtc,
            acceptedPayload.Permissions);
        Assert.True(windowsKey.Verify(
            SecurityEncoding.Base64UrlDecode(qr.WindowsIdentityPublicKey),
            acceptedTranscript,
            SecurityEncoding.Base64UrlDecode(acceptedPayload.WindowsSignature)));

        Assert.True(await handler.HandleCompleteAckAsync(CompleteAck(qr.SessionId)));
        var active = await trusted.GetTrustedDeviceAsync("android-test");
        Assert.Equal(TrustStatuses.Active, active?.TrustStatus);
        Assert.Null(manager.CurrentSession);
    }

    [Fact]
    public async Task InvalidConfirmSignature_IsRejectedAndDoesNotSaveTrust()
    {
        using var windowsKey = new FakeDeviceIdentityKeyProvider();
        var identity = new FakeIdentityProvider("windows-test");
        var manager = new PairingSessionManager(identity, windowsKey);
        var trusted = new FakeTrustedDeviceRepository();
        var qr = await manager.StartPairingAsync(54321, ["127.0.0.1"]);
        using var androidKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var androidPublicKey = androidKey.ExportSubjectPublicKeyInfo();
        var androidFingerprint = SecurityEncoding.Fingerprint(androidPublicKey);
        var androidNonce = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var proofTranscript = TranscriptBuilder.PairingRequest(
            qr.SessionId,
            qr.WindowsDeviceId,
            "android-test",
            qr.WindowsIdentityFingerprint,
            androidFingerprint,
            androidNonce);
        var proof = SecurityEncoding.Base64UrlEncode(SecurityEncoding.HmacSha256(manager.CurrentSession!.PairingSecret, proofTranscript));
        var handler = new PairingRequestHandler(manager, identity, windowsKey, trusted);

        await handler.HandleAsync(Request(qr.SessionId, androidPublicKey, androidFingerprint, androidNonce, proof));
        manager.ConfirmLocalUser();
        var rejected = await handler.HandleConfirmAsync(Confirm(qr.SessionId, RandomNumberGenerator.GetBytes(64)));

        Assert.Equal(ProtocolMessageTypes.PairingRejected, rejected.Type);
        Assert.Null(await trusted.GetTrustedDeviceAsync("android-test"));
    }

    private static ProtocolMessage Request(
        string sessionId,
        byte[] androidPublicKey,
        string androidFingerprint,
        string androidNonce,
        string proof)
    {
        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = "pair-request-1",
            Type = ProtocolMessageTypes.PairingRequest,
            SenderDeviceId = "android-test",
            RecipientDeviceId = "windows-test",
            TimestampUtc = "2026-07-06T12:00:00Z",
            Payload = ProtocolSerializer.PayloadToJson(new PairingRequestPayload
            {
                SessionId = sessionId,
                AndroidDeviceId = "android-test",
                AndroidDeviceName = "Pixel",
                AndroidAppVersion = "1.0",
                AndroidIdentityPublicKey = SecurityEncoding.Base64UrlEncode(androidPublicKey),
                AndroidIdentityFingerprint = androidFingerprint,
                AndroidNonce = androidNonce,
                Proof = proof,
            }),
        };
    }

    private static ProtocolMessage Confirm(string sessionId, byte[] signature) => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "pair-confirm-1",
        Type = ProtocolMessageTypes.PairingConfirm,
        SenderDeviceId = "android-test",
        RecipientDeviceId = "windows-test",
        TimestampUtc = "2026-07-06T12:00:02Z",
        Payload = ProtocolSerializer.PayloadToJson(new PairingConfirmPayload
        {
            SessionId = sessionId,
            Confirmed = true,
            AndroidSignature = SecurityEncoding.Base64UrlEncode(signature),
        }),
    };

    private static ProtocolMessage CompleteAck(string sessionId) => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = "pair-complete-1",
        Type = ProtocolMessageTypes.PairingCompleteAck,
        SenderDeviceId = "android-test",
        RecipientDeviceId = "windows-test",
        TimestampUtc = "2026-07-06T12:00:03Z",
        Payload = ProtocolSerializer.PayloadToJson(new PairingCompleteAckPayload
        {
            SessionId = sessionId,
            Status = "stored",
        }),
    };
}
