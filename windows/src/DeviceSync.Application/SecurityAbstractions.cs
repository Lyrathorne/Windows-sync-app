namespace DeviceSync.Application;

public interface IDeviceIdentityKeyProvider
{
    Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default);
    Task<string> GetPublicKeyFingerprintAsync(CancellationToken cancellationToken = default);
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);
}

public interface IProtectedKeyStorage
{
    Task<byte[]?> ReadProtectedAsync(CancellationToken cancellationToken = default);
    Task WriteProtectedAtomicAsync(byte[] protectedBytes, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface IDataProtector
{
    byte[] Protect(byte[] plainBytes);
    byte[] Unprotect(byte[] protectedBytes);
}

public interface IQrCodeGenerator
{
    byte[] GeneratePng(string content, int pixelsPerModule);
}

public interface IPairingSessionManager
{
    PairingState State { get; }
    PairingSession? CurrentSession { get; }
    PairingQrPayload? CurrentQrPayload { get; }
    event EventHandler? StateChanged;
    Task<PairingQrPayload> StartPairingAsync(int port, IReadOnlyList<string> hostAddresses, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
    PairingSession? ConsumeIfProofValid(string sessionId, byte[] proof, byte[] transcript);
    void MarkChallengeSent(string sessionId, string androidDeviceId, string androidDeviceName, string androidPublicKey, string androidFingerprint, string androidNonce, string verificationCode);
    void ConfirmLocalUser();
    void ConfirmRemoteAndroid(string sessionId);
    void CompletePairing(string sessionId);
    bool IsReadyForAccepted(string sessionId);
}
