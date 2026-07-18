using System.Security.Cryptography;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed class PairingSessionManager : IPairingSessionManager
{
    private readonly IWindowsDeviceIdentityProvider _deviceIdentityProvider;
    private readonly IDeviceIdentityKeyProvider _keyProvider;
    private readonly object _gate = new();

    public PairingSessionManager(
        IWindowsDeviceIdentityProvider deviceIdentityProvider,
        IDeviceIdentityKeyProvider keyProvider)
    {
        _deviceIdentityProvider = deviceIdentityProvider;
        _keyProvider = keyProvider;
    }

    public PairingState State { get; private set; } = PairingState.Disabled;
    public PairingSession? CurrentSession { get; private set; }
    public PairingQrPayload? CurrentQrPayload { get; private set; }
    public event EventHandler? StateChanged;

    public async Task<PairingQrPayload> StartPairingAsync(
        int port,
        IReadOnlyList<string> hostAddresses,
        CancellationToken cancellationToken = default)
    {
        if (hostAddresses.Count == 0)
        {
            throw new InvalidOperationException("Не найден адрес локальной сети");
        }

        SetState(PairingState.Starting);
        var settings = await _deviceIdentityProvider.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var deviceId = await _deviceIdentityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var publicKey = await _keyProvider.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = await _keyProvider.GetPublicKeyFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var tlsFingerprint = _keyProvider is ITlsCertificateProvider tlsCertificateProvider
            ? await tlsCertificateProvider.GetServerSpkiFingerprintAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var session = new PairingSession
        {
            SessionId = $"pair-{Guid.NewGuid()}",
            PairingSecret = RandomNumberGenerator.GetBytes(32),
            WindowsNonce = RandomNumberGenerator.GetBytes(32),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            // Five minutes tolerates a slow camera launch and modest clock skew while
            // keeping the one-time secret short-lived.
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        var payload = new PairingQrPayload
        {
            Format = "devicesync-pairing",
            Version = 1,
            SessionId = session.SessionId,
            PairingSecret = SecurityEncoding.Base64UrlEncode(session.PairingSecret),
            ExpiresAtUtc = session.ExpiresAtUtc.ToString("O"),
            HostAddresses = hostAddresses,
            Port = port,
            WindowsDeviceId = deviceId,
            WindowsDeviceName = settings.DeviceName ?? Environment.MachineName,
            WindowsIdentityPublicKey = SecurityEncoding.Base64UrlEncode(publicKey),
            WindowsIdentityFingerprint = fingerprint,
            TlsServerSpkiFingerprint = tlsFingerprint,
            ProtocolMin = ProtocolConstants.ProtocolVersion,
            ProtocolMax = ProtocolConstants.ProtocolVersion,
        };

        lock (_gate)
        {
            CurrentSession = session;
            CurrentQrPayload = payload;
        }

        SetState(PairingState.WaitingForDevice);
        return payload;
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ClearSecret(CurrentSession);
            CurrentSession = null;
            CurrentQrPayload = null;
        }

        SetState(PairingState.Disabled);
        return Task.CompletedTask;
    }

    public PairingSession? ConsumeIfProofValid(string sessionId, byte[] proof, byte[] transcript)
    {
        lock (_gate)
        {
            var session = CurrentSession;
            if (session is null || session.SessionId != sessionId || session.IsConsumed || DateTimeOffset.UtcNow > session.ExpiresAtUtc)
            {
                SetState(PairingState.Expired);
                return null;
            }

            var expected = SecurityEncoding.HmacSha256(session.PairingSecret, transcript);
            if (!SecurityEncoding.FixedTimeEquals(expected, proof))
            {
                var failed = session with { FailedAttemptCount = session.FailedAttemptCount + 1 };
                CurrentSession = failed.FailedAttemptCount >= 5 ? null : failed;
                if (failed.FailedAttemptCount >= 5)
                {
                    ClearSecret(failed);
                    SetState(PairingState.Rejected);
                }
                return null;
            }

            var consumed = session with { RequestHmacVerified = true };
            CurrentSession = consumed;
            SetState(PairingState.ProofVerified);
            return consumed;
        }
    }

    public void MarkChallengeSent(
        string sessionId,
        string androidDeviceId,
        string androidDeviceName,
        string androidPublicKey,
        string androidFingerprint,
        string androidNonce,
        string verificationCode)
    {
        lock (_gate)
        {
            if (CurrentSession is null || CurrentSession.SessionId != sessionId || CurrentSession.IsConsumed)
            {
                return;
            }

            CurrentSession = CurrentSession with
            {
                ChallengeSent = true,
                AndroidDeviceId = androidDeviceId,
                AndroidDeviceName = androidDeviceName,
                AndroidIdentityPublicKey = androidPublicKey,
                AndroidIdentityFingerprint = androidFingerprint,
                AndroidNonce = androidNonce,
                VerificationCode = verificationCode,
            };
            SetState(PairingState.WaitingForUserConfirmation);
        }
    }

    public void ConfirmLocalUser()
    {
        lock (_gate)
        {
            if (CurrentSession is null || CurrentSession.IsConsumed || DateTimeOffset.UtcNow > CurrentSession.ExpiresAtUtc)
            {
                return;
            }

            CurrentSession = CurrentSession with { LocalUserConfirmed = true };
            SetState(IsReadyForAccepted(CurrentSession.SessionId) ? PairingState.Completing : PairingState.WaitingForUserConfirmation);
        }
    }

    public void ConfirmRemoteAndroid(string sessionId)
    {
        lock (_gate)
        {
            if (CurrentSession is null || CurrentSession.SessionId != sessionId || CurrentSession.IsConsumed)
            {
                return;
            }

            CurrentSession = CurrentSession with
            {
                RemoteAndroidConfirmed = true,
                AndroidSignatureVerified = true,
            };
            SetState(IsReadyForAccepted(sessionId) ? PairingState.Completing : PairingState.WaitingForUserConfirmation);
        }
    }

    public bool IsReadyForAccepted(string sessionId)
    {
        lock (_gate)
        {
            var session = CurrentSession;
            if (session is not null &&
                session.SessionId == sessionId &&
                DateTimeOffset.UtcNow > session.ExpiresAtUtc)
            {
                SetState(PairingState.Expired);
                return false;
            }
            return session is not null &&
                session.SessionId == sessionId &&
                session.RequestHmacVerified &&
                session.ChallengeSent &&
                session.LocalUserConfirmed &&
                session.RemoteAndroidConfirmed &&
                session.AndroidSignatureVerified &&
                !session.IsConsumed &&
                DateTimeOffset.UtcNow <= session.ExpiresAtUtc;
        }
    }

    public void CompletePairing(string sessionId)
    {
        lock (_gate)
        {
            if (CurrentSession is null || CurrentSession.SessionId != sessionId)
            {
                return;
            }

            CurrentSession = CurrentSession with { IsConsumed = true };
            ClearSecret(CurrentSession);
            CurrentSession = null;
            CurrentQrPayload = null;
            SetState(PairingState.Completed);
        }
    }

    private void SetState(PairingState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ClearSecret(PairingSession? session)
    {
        if (session is null) return;
        CryptographicOperations.ZeroMemory(session.PairingSecret);
        CryptographicOperations.ZeroMemory(session.WindowsNonce);
    }
}
