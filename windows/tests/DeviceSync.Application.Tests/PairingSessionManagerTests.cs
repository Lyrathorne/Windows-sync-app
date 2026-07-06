using DeviceSync.Application;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class PairingSessionManagerTests
{
    [Fact]
    public async Task StartPairing_CreatesOneTimeSession()
    {
        var manager = Manager();

        var qr = await manager.StartPairingAsync(54321, ["192.168.1.25"]);

        Assert.Equal(PairingState.WaitingForDevice, manager.State);
        Assert.Equal(32, manager.CurrentSession?.PairingSecret.Length);
        Assert.Equal(32, manager.CurrentSession?.WindowsNonce.Length);
        Assert.Equal(qr.SessionId, manager.CurrentSession?.SessionId);
        Assert.Equal("devicesync-pairing", qr.Format);
    }

    [Fact]
    public async Task Proof_DoesNotConsumeSessionBeforeCompleteAck()
    {
        var manager = Manager();
        var qr = await manager.StartPairingAsync(54321, ["192.168.1.25"]);
        var transcript = TranscriptBuilder.PairingRequest(
            qr.SessionId,
            qr.WindowsDeviceId,
            "android-test",
            qr.WindowsIdentityFingerprint,
            "android-fingerprint",
            "android-nonce");
        var proof = SecurityEncoding.HmacSha256(manager.CurrentSession!.PairingSecret, transcript);

        Assert.NotNull(manager.ConsumeIfProofValid(qr.SessionId, proof, transcript));
        Assert.False(manager.CurrentSession!.IsConsumed);
        manager.CompletePairing(qr.SessionId);
        Assert.Null(manager.CurrentSession);
    }

    [Fact]
    public async Task WrongProof_IncrementsAttemptsAndRejectsAfterFive()
    {
        var manager = Manager();
        var qr = await manager.StartPairingAsync(54321, ["192.168.1.25"]);

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(manager.ConsumeIfProofValid(qr.SessionId, new byte[32], new byte[] { 1, 2, 3 }));
        }

        Assert.Equal(PairingState.Rejected, manager.State);
        Assert.Null(manager.CurrentSession);
    }

    private static PairingSessionManager Manager()
    {
        return new PairingSessionManager(
            new FakeIdentityProvider("windows-test"),
            new FakeKeyProvider());
    }
}

internal sealed class FakeKeyProvider : IDeviceIdentityKeyProvider
{
    private readonly byte[] _publicKey = "windows-test-public-key-spki"u8.ToArray();

    public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_publicKey);
    public Task<string> GetPublicKeyFingerprintAsync(CancellationToken cancellationToken = default) => Task.FromResult(SecurityEncoding.Fingerprint(_publicKey));
    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.FromResult(data.ToArray());
    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) => data.SequenceEqual(signature);
}
