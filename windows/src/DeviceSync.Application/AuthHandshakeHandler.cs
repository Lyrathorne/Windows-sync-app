using System.Security.Cryptography;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed record AuthChallengeResult(ProtocolMessage Response, AuthAttempt? Attempt);

public sealed record AuthAttempt
{
    public required string HelloMessageId { get; init; }
    public required string AndroidDeviceId { get; init; }
    public required string AndroidDeviceName { get; init; }
    public required string AndroidFingerprint { get; init; }
    public required string AndroidPublicKey { get; init; }
    public required string WindowsDeviceId { get; init; }
    public required string WindowsFingerprint { get; init; }
    public required string ClientNonce { get; init; }
    public required string ServerNonce { get; init; }
    public required byte[] Transcript { get; init; }
    public required int ProtocolVersion { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool ResponseUsed { get; set; }
}

public sealed class AuthHandshakeHandler
{
    private readonly IWindowsDeviceIdentityProvider _identityProvider;
    private readonly IDeviceIdentityKeyProvider _keyProvider;
    private readonly ITrustedDeviceRepository _trustedDeviceRepository;
    private readonly IPairingSessionManager? _pairingSessionManager;
    private readonly string _windowsDeviceName;

    public AuthHandshakeHandler(
        IWindowsDeviceIdentityProvider identityProvider,
        IDeviceIdentityKeyProvider keyProvider,
        ITrustedDeviceRepository trustedDeviceRepository,
        string? windowsDeviceName = null,
        IPairingSessionManager? pairingSessionManager = null)
    {
        _identityProvider = identityProvider;
        _keyProvider = keyProvider;
        _trustedDeviceRepository = trustedDeviceRepository;
        _pairingSessionManager = pairingSessionManager;
        _windowsDeviceName = string.IsNullOrWhiteSpace(windowsDeviceName)
            ? Environment.MachineName
            : windowsDeviceName;
    }

    public async Task<AuthChallengeResult> BuildChallengeAsync(ProtocolMessage hello, CancellationToken cancellationToken = default)
    {
        if (hello.Type != ProtocolMessageTypes.ConnectionHello)
        {
            throw new ProtocolException("The first auth message must be connection.hello.");
        }

        if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion || string.IsNullOrWhiteSpace(hello.MessageId) || string.IsNullOrWhiteSpace(hello.SenderDeviceId))
        {
            return Rejected(hello, ProtocolErrorCodes.UnsupportedProtocolVersion);
        }

        var payload = ProtocolSerializer.DecodePayload<ConnectionHelloPayload>(hello.Payload);
        var negotiatedVersion = ProtocolVersionNegotiator.Negotiate(
            payload.ProtocolVersion,
            payload.ProtocolMin,
            payload.ProtocolMax);
        if (negotiatedVersion is null || payload.DeviceType != "android")
        {
            return Rejected(hello, ProtocolErrorCodes.UnsupportedProtocolVersion);
        }

        if (string.IsNullOrWhiteSpace(payload.IdentityFingerprint) ||
            string.IsNullOrWhiteSpace(payload.ClientNonce) ||
            payload.AuthVersion != 1 ||
            !HasNonceLength(payload.ClientNonce))
        {
            return Rejected(hello, "AUTH_HELLO_INVALID");
        }

        var trusted = await _trustedDeviceRepository.GetTrustedDeviceAsync(hello.SenderDeviceId, cancellationToken).ConfigureAwait(false);
        if (trusted is null)
        {
            return Rejected(hello, "PAIRING_REQUIRED");
        }

        if (trusted.TrustStatus == TrustStatuses.Revoked || trusted.RevokedAtUtc is not null)
        {
            // Starting a QR flow is an explicit user action. During that short-lived
            // window tell a previously revoked client to enter pairing mode instead
            // of trapping it in its old authenticated-reconnect loop.
            var pairing = _pairingSessionManager?.CurrentSession;
            if (pairing is not null &&
                !pairing.IsConsumed &&
                DateTimeOffset.UtcNow <= pairing.ExpiresAtUtc)
            {
                return Rejected(hello, "PAIRING_REQUIRED");
            }
            return Rejected(hello, "TRUST_REVOKED");
        }

        if (trusted.TrustStatus != TrustStatuses.Active)
        {
            return Rejected(hello, "PAIRING_REQUIRED");
        }

        if (!string.Equals(trusted.IdentityFingerprint, payload.IdentityFingerprint, StringComparison.Ordinal))
        {
            return Rejected(hello, "IDENTITY_KEY_CHANGED");
        }

        var windowsDeviceId = await _identityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var windowsFingerprint = await _keyProvider.GetPublicKeyFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var serverNonce = SecurityEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var transcript = TranscriptBuilder.SessionAuth(
            negotiatedVersion.Value,
            hello.SenderDeviceId,
            windowsDeviceId,
            payload.IdentityFingerprint,
            windowsFingerprint,
            payload.ClientNonce,
            serverNonce,
            hello.MessageId);
        var signature = SecurityEncoding.Base64UrlEncode(await _keyProvider.SignAsync(transcript, cancellationToken).ConfigureAwait(false));
        var attempt = new AuthAttempt
        {
            HelloMessageId = hello.MessageId,
            AndroidDeviceId = hello.SenderDeviceId,
            AndroidDeviceName = payload.DeviceName,
            AndroidFingerprint = payload.IdentityFingerprint,
            AndroidPublicKey = trusted.IdentityPublicKey,
            WindowsDeviceId = windowsDeviceId,
            WindowsFingerprint = windowsFingerprint,
            ClientNonce = payload.ClientNonce,
            ServerNonce = serverNonce,
            Transcript = transcript,
            ProtocolVersion = negotiatedVersion.Value,
            Capabilities = CapabilityNegotiator.Intersect(payload.Capabilities),
        };

        return new AuthChallengeResult(new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.AuthChallenge,
            SenderDeviceId = windowsDeviceId,
            RecipientDeviceId = hello.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = hello.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new AuthChallengePayload
            {
                ServerNonce = serverNonce,
                WindowsIdentityFingerprint = windowsFingerprint,
                ServerSignature = signature,
                HelloMessageId = hello.MessageId,
                AcceptedProtocolVersion = negotiatedVersion.Value,
                Capabilities = SupportedCapabilities.Values,
            }),
        }, attempt);
    }

    public async Task<AuthVerifyResult> VerifyResponseAsync(AuthAttempt attempt, ProtocolMessage response, CancellationToken cancellationToken = default)
    {
        if (attempt.ResponseUsed || DateTimeOffset.UtcNow - attempt.CreatedAtUtc > TimeSpan.FromSeconds(15))
        {
            return AuthVerifyResult.Rejected(BuildAuthRejected(response, attempt.WindowsDeviceId, "AUTH_ATTEMPT_EXPIRED"));
        }

        if (response.Type != ProtocolMessageTypes.AuthResponse || response.SenderDeviceId != attempt.AndroidDeviceId)
        {
            return AuthVerifyResult.Rejected(BuildAuthRejected(response, attempt.WindowsDeviceId, "AUTH_ORDER_INVALID"));
        }

        var payload = ProtocolSerializer.DecodePayload<AuthResponsePayload>(response.Payload);
        if (payload.HelloMessageId != attempt.HelloMessageId)
        {
            return AuthVerifyResult.Rejected(BuildAuthRejected(response, attempt.WindowsDeviceId, "AUTH_HELLO_MISMATCH"));
        }

        attempt.ResponseUsed = true;
        var ok = _keyProvider.Verify(
            SecurityEncoding.Base64UrlDecode(attempt.AndroidPublicKey),
            attempt.Transcript,
            SecurityEncoding.Base64UrlDecode(payload.ClientSignature));
        if (!ok)
        {
            return AuthVerifyResult.Rejected(BuildAuthRejected(response, attempt.WindowsDeviceId, "AUTH_SIGNATURE_INVALID"));
        }

        await _trustedDeviceRepository.UpdateLastVerifiedAtAsync(attempt.AndroidDeviceId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        var session = new DeviceSessionInfo
        {
            DeviceId = attempt.AndroidDeviceId,
            DeviceName = attempt.AndroidDeviceName,
            DeviceType = "android",
            ProtocolVersion = attempt.ProtocolVersion,
            Capabilities = attempt.Capabilities,
            ConnectedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow,
        };
        var accepted = new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.AuthAccepted,
            SenderDeviceId = attempt.WindowsDeviceId,
            RecipientDeviceId = attempt.AndroidDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = response.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new AuthAcceptedPayload
            {
                Status = "accepted",
                AcceptedProtocolVersion = attempt.ProtocolVersion,
                Capabilities = SupportedCapabilities.Values,
            }),
        };
        return AuthVerifyResult.Accepted(accepted, session);
    }

    private static AuthChallengeResult Rejected(ProtocolMessage hello, string code)
    {
        return new AuthChallengeResult(new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.ProtocolError,
            SenderDeviceId = "windows",
            RecipientDeviceId = hello.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = hello.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new ProtocolErrorPayload
            {
                Code = code,
                Message = code,
                Fatal = true,
            }),
        }, null);
    }

    private static ProtocolMessage BuildAuthRejected(ProtocolMessage response, string windowsDeviceId, string code)
    {
        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.AuthRejected,
            SenderDeviceId = windowsDeviceId,
            RecipientDeviceId = response.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = response.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new ProtocolErrorPayload
            {
                Code = code,
                Message = code,
                Fatal = true,
            }),
        };
    }

    private static bool HasNonceLength(string nonce)
    {
        try
        {
            return SecurityEncoding.Base64UrlDecode(nonce).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record AuthVerifyResult(ProtocolMessage Response, DeviceSessionInfo? Session)
{
    public bool IsAccepted => Session is not null;
    public static AuthVerifyResult Accepted(ProtocolMessage response, DeviceSessionInfo session) => new(response, session);
    public static AuthVerifyResult Rejected(ProtocolMessage response) => new(response, null);
}
