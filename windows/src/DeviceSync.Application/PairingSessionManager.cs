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
        SetState(PairingState.Starting);
        var settings = await _deviceIdentityProvider.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var deviceId = await _deviceIdentityProvider.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
        var publicKey = await _keyProvider.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = await _keyProvider.GetPublicKeyFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var session = new PairingSession
        {
            SessionId = $"pair-{Guid.NewGuid()}",
            PairingSecret = RandomNumberGenerator.GetBytes(32),
            WindowsNonce = RandomNumberGenerator.GetBytes(32),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
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

            var consumed = session with { IsConsumed = true };
            CurrentSession = consumed;
            SetState(PairingState.ProofVerified);
            return consumed;
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
