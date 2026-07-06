using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed record PairingRequestResult(ProtocolMessage Response, bool Accepted);

public sealed class PairingRequestHandler
{
    private readonly IPairingSessionManager _pairingSessionManager;
    private readonly IWindowsDeviceIdentityProvider _windowsIdentityProvider;
    private readonly IDeviceIdentityKeyProvider _keyProvider;
    private readonly ITrustedDeviceRepository _trustedDeviceRepository;

    public PairingRequestHandler(
        IPairingSessionManager pairingSessionManager,
        IWindowsDeviceIdentityProvider windowsIdentityProvider,
        IDeviceIdentityKeyProvider keyProvider,
        ITrustedDeviceRepository trustedDeviceRepository)
    {
        _pairingSessionManager = pairingSessionManager;
        _windowsIdentityProvider = windowsIdentityProvider;
        _keyProvider = keyProvider;
        _trustedDeviceRepository = trustedDeviceRepository;
    }

    public async Task<PairingRequestResult> HandleAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Type != ProtocolMessageTypes.PairingRequest)
        {
            throw new ProtocolException("The first pairing message must be pairing.request.");
        }

        var request = ProtocolSerializer.DecodePayload<PairingRequestPayload>(message.Payload);
        var qr = _pairingSessionManager.CurrentQrPayload;
        if (qr is null || _pairingSessionManager.CurrentSession is null)
        {
            return Rejected(request, "PAIRING_UNAVAILABLE");
        }

        if (!IsValidRequestShape(request))
        {
            return Rejected(request, "PAIRING_REJECTED");
        }

        var androidPublicKey = SecurityEncoding.Base64UrlDecode(request.AndroidIdentityPublicKey);
        if (SecurityEncoding.Fingerprint(androidPublicKey) != request.AndroidIdentityFingerprint)
        {
            return Rejected(request, "PAIRING_REJECTED");
        }

        var transcript = TranscriptBuilder.PairingRequest(
            request.SessionId,
            qr.WindowsDeviceId,
            request.AndroidDeviceId,
            qr.WindowsIdentityFingerprint,
            request.AndroidIdentityFingerprint,
            request.AndroidNonce);
        var proof = SecurityEncoding.Base64UrlDecode(request.Proof);
        var session = _pairingSessionManager.ConsumeIfProofValid(request.SessionId, proof, transcript);
        if (session is null)
        {
            return Rejected(request, "PAIRING_REJECTED");
        }

        var windowsPublicKey = await _keyProvider.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        var windowsFingerprint = await _keyProvider.GetPublicKeyFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var settings = await _windowsIdentityProvider.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var windowsDeviceId = await _windowsIdentityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var windowsNonce = SecurityEncoding.Base64UrlEncode(session.WindowsNonce);
        var challengeTranscript = TranscriptBuilder.PairingChallenge(
            request.SessionId,
            windowsDeviceId,
            request.AndroidDeviceId,
            windowsFingerprint,
            request.AndroidIdentityFingerprint,
            request.AndroidNonce,
            windowsNonce);
        var challengeProof = SecurityEncoding.Base64UrlEncode(SecurityEncoding.HmacSha256(session.PairingSecret, challengeTranscript));
        var verificationCode = SecurityEncoding.VerificationCode(
            request.SessionId,
            windowsDeviceId,
            request.AndroidDeviceId,
            windowsFingerprint,
            request.AndroidIdentityFingerprint,
            request.AndroidNonce,
            windowsNonce);
        _pairingSessionManager.MarkChallengeSent(
            request.SessionId,
            request.AndroidDeviceId,
            request.AndroidDeviceName,
            request.AndroidIdentityPublicKey,
            request.AndroidIdentityFingerprint,
            request.AndroidNonce,
            verificationCode);

        var response = new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.PairingChallenge,
            SenderDeviceId = windowsDeviceId,
            RecipientDeviceId = request.AndroidDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = message.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new PairingChallengePayload
            {
                SessionId = request.SessionId,
                WindowsDeviceId = windowsDeviceId,
                WindowsDeviceName = settings.DeviceName ?? Environment.MachineName,
                WindowsIdentityPublicKey = SecurityEncoding.Base64UrlEncode(windowsPublicKey),
                WindowsIdentityFingerprint = windowsFingerprint,
                WindowsNonce = windowsNonce,
                AndroidNonce = request.AndroidNonce,
                Proof = challengeProof,
            }),
        };
        return new PairingRequestResult(response, Accepted: true);
    }

    public async Task<ProtocolMessage> HandleConfirmAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Type != ProtocolMessageTypes.PairingConfirm)
        {
            return ProtocolError(message, "PAIRING_ORDER");
        }

        var confirm = ProtocolSerializer.DecodePayload<PairingConfirmPayload>(message.Payload);
        var session = _pairingSessionManager.CurrentSession;
        var qr = _pairingSessionManager.CurrentQrPayload;
        if (session is null || qr is null || session.SessionId != confirm.SessionId || !confirm.Confirmed)
        {
            return Rejected(confirm.SessionId, message.SenderDeviceId);
        }

        if (!RequiredSessionFieldsPresent(session))
        {
            return Rejected(confirm.SessionId, message.SenderDeviceId);
        }

        var transcript = TranscriptBuilder.PairingConfirmation(
            session.SessionId,
            qr.WindowsDeviceId,
            session.AndroidDeviceId!,
            qr.WindowsIdentityFingerprint,
            session.AndroidIdentityFingerprint!,
            session.AndroidNonce!,
            SecurityEncoding.Base64UrlEncode(session.WindowsNonce),
            session.VerificationCode!);
        var signatureOk = _keyProvider.Verify(
            SecurityEncoding.Base64UrlDecode(session.AndroidIdentityPublicKey!),
            transcript,
            SecurityEncoding.Base64UrlDecode(confirm.AndroidSignature));
        if (!signatureOk)
        {
            await _pairingSessionManager.CancelAsync(cancellationToken).ConfigureAwait(false);
            return Rejected(confirm.SessionId, message.SenderDeviceId);
        }

        _pairingSessionManager.ConfirmRemoteAndroid(confirm.SessionId);
        if (!_pairingSessionManager.IsReadyForAccepted(confirm.SessionId))
        {
            return ProtocolError(message, "WAITING_FOR_LOCAL_CONFIRMATION", fatal: false);
        }

        return await BuildAcceptedAsync(message, session, qr, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HandleCompleteAckAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Type != ProtocolMessageTypes.PairingCompleteAck)
        {
            return false;
        }

        var ack = ProtocolSerializer.DecodePayload<PairingCompleteAckPayload>(message.Payload);
        var session = _pairingSessionManager.CurrentSession;
        if (session is null || session.SessionId != ack.SessionId || ack.Status != "stored" || session.AndroidDeviceId is null)
        {
            return false;
        }

        await _trustedDeviceRepository.ActivateTrustedDeviceAsync(session.AndroidDeviceId, cancellationToken).ConfigureAwait(false);
        _pairingSessionManager.CompletePairing(ack.SessionId);
        return true;
    }

    public async Task ExpirePendingTrustAsync(CancellationToken cancellationToken = default)
    {
        var session = _pairingSessionManager.CurrentSession;
        if (session?.AndroidDeviceId is not null)
        {
            await _trustedDeviceRepository.DeleteAsync(session.AndroidDeviceId, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool CanBuildAccepted(string sessionId)
    {
        return _pairingSessionManager.IsReadyForAccepted(sessionId);
    }

    public async Task<ProtocolMessage> BuildAcceptedForCurrentSessionAsync(ProtocolMessage correlation, CancellationToken cancellationToken = default)
    {
        var session = _pairingSessionManager.CurrentSession ?? throw new ProtocolException("Pairing session is missing.");
        var qr = _pairingSessionManager.CurrentQrPayload ?? throw new ProtocolException("Pairing QR payload is missing.");
        return await BuildAcceptedAsync(correlation, session, qr, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProtocolMessage> BuildAcceptedAsync(
        ProtocolMessage correlation,
        PairingSession session,
        PairingQrPayload qr,
        CancellationToken cancellationToken)
    {
        var pairedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        var permissions = new[] { "basic_connection", "heartbeat" };
        await _trustedDeviceRepository.SaveTrustedDeviceAsync(new TrustedDevice
        {
            DeviceId = session.AndroidDeviceId!,
            DeviceName = session.AndroidDeviceName!,
            IdentityPublicKey = session.AndroidIdentityPublicKey!,
            IdentityFingerprint = session.AndroidIdentityFingerprint!,
            FutureTlsCertificateFingerprint = null,
            PairedAtUtc = DateTimeOffset.Parse(pairedAtUtc),
            LastVerifiedAtUtc = null,
            RevokedAtUtc = null,
            TrustStatus = TrustStatuses.Pending,
        }, cancellationToken).ConfigureAwait(false);

        var transcript = TranscriptBuilder.PairingAccepted(
            session.SessionId,
            qr.WindowsDeviceId,
            session.AndroidDeviceId!,
            qr.WindowsIdentityFingerprint,
            session.AndroidIdentityFingerprint!,
            session.AndroidNonce!,
            SecurityEncoding.Base64UrlEncode(session.WindowsNonce),
            session.VerificationCode!,
            pairedAtUtc,
            permissions);
        var signature = SecurityEncoding.Base64UrlEncode(await _keyProvider.SignAsync(transcript, cancellationToken).ConfigureAwait(false));
        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.PairingAccepted,
            SenderDeviceId = qr.WindowsDeviceId,
            RecipientDeviceId = session.AndroidDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = correlation.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new PairingAcceptedPayload
            {
                SessionId = session.SessionId,
                WindowsSignature = signature,
                PairedAtUtc = pairedAtUtc,
                Permissions = permissions,
            }),
        };
    }

    private static bool IsValidRequestShape(PairingRequestPayload request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.SessionId)) return false;
            if (string.IsNullOrWhiteSpace(request.AndroidDeviceId)) return false;
            if (request.AndroidDeviceName.Length is < 1 or > 80) return false;
            if (SecurityEncoding.Base64UrlDecode(request.AndroidNonce).Length != 32) return false;
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(SecurityEncoding.Base64UrlDecode(request.AndroidIdentityPublicKey), out _);
            return true;
        }
        catch (Exception error) when (error is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static bool RequiredSessionFieldsPresent(PairingSession session)
    {
        return session.RequestHmacVerified &&
            session.ChallengeSent &&
            !session.IsConsumed &&
            DateTimeOffset.UtcNow <= session.ExpiresAtUtc &&
            !string.IsNullOrWhiteSpace(session.AndroidDeviceId) &&
            !string.IsNullOrWhiteSpace(session.AndroidDeviceName) &&
            !string.IsNullOrWhiteSpace(session.AndroidIdentityPublicKey) &&
            !string.IsNullOrWhiteSpace(session.AndroidIdentityFingerprint) &&
            !string.IsNullOrWhiteSpace(session.AndroidNonce) &&
            !string.IsNullOrWhiteSpace(session.VerificationCode);
    }

    private static PairingRequestResult Rejected(PairingRequestPayload request, string code)
    {
        return new PairingRequestResult(
            new ProtocolMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MessageId = Guid.NewGuid().ToString(),
                Type = ProtocolMessageTypes.PairingRejected,
                SenderDeviceId = "windows",
                RecipientDeviceId = request.AndroidDeviceId,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Payload = ProtocolSerializer.PayloadToJson(new ProtocolErrorPayload
                {
                    Code = code,
                    Message = "Pairing request was rejected.",
                    Fatal = true,
                }),
            },
            Accepted: false);
    }

    private static ProtocolMessage Rejected(string sessionId, string recipientDeviceId)
    {
        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.PairingRejected,
            SenderDeviceId = "windows",
            RecipientDeviceId = recipientDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            Payload = ProtocolSerializer.PayloadToJson(new ProtocolErrorPayload
            {
                Code = "PAIRING_REJECTED",
                Message = "Pairing confirmation was rejected.",
                Fatal = true,
            }),
            CorrelationId = sessionId,
        };
    }

    private static ProtocolMessage ProtocolError(ProtocolMessage message, string code, bool fatal = true)
    {
        return new ProtocolMessage
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageId = Guid.NewGuid().ToString(),
            Type = ProtocolMessageTypes.ProtocolError,
            SenderDeviceId = "windows",
            RecipientDeviceId = message.SenderDeviceId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = message.MessageId,
            Payload = ProtocolSerializer.PayloadToJson(new ProtocolErrorPayload
            {
                Code = code,
                Message = "Pairing message cannot be processed yet.",
                Fatal = fatal,
            }),
        };
    }
}
