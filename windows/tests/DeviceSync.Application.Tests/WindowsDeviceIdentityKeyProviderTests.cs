using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class WindowsDeviceIdentityKeyProviderTests
{
    [Fact]
    public async Task IdentityKey_PersistsAcrossProviderInstances()
    {
        var storage = new MemoryProtectedKeyStorage();
        var protector = new PassThroughProtector();
        var first = new WindowsDeviceIdentityKeyProvider(storage, protector);
        var firstPublic = await first.GetPublicKeyAsync();

        var second = new WindowsDeviceIdentityKeyProvider(storage, protector);
        var secondPublic = await second.GetPublicKeyAsync();

        Assert.Equal(firstPublic, secondPublic);
        Assert.Equal(await first.GetPublicKeyFingerprintAsync(), await second.GetPublicKeyFingerprintAsync());
    }

    [Fact]
    public async Task Signature_VerifiesWithPublicKey()
    {
        var provider = new WindowsDeviceIdentityKeyProvider(new MemoryProtectedKeyStorage(), new PassThroughProtector());
        var publicKey = await provider.GetPublicKeyAsync();
        var data = "hello"u8.ToArray();

        var signature = await provider.SignAsync(data);

        Assert.True(provider.Verify(publicKey, data, signature));
        Assert.False(provider.Verify(publicKey, "changed"u8, signature));

        using var externalVerifier = ECDsa.Create();
        externalVerifier.ImportSubjectPublicKeyInfo(publicKey, out _);
        Assert.True(externalVerifier.VerifyData(
            data,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void Verify_AcceptsDerSignatureProducedByExternalClient()
    {
        using var androidKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = "android-pairing-confirm"u8.ToArray();
        var signature = androidKey.SignData(
            data,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var provider = new WindowsDeviceIdentityKeyProvider(new MemoryProtectedKeyStorage(), new PassThroughProtector());

        Assert.True(provider.Verify(androidKey.ExportSubjectPublicKeyInfo(), data, signature));
    }

    [Fact]
    public async Task CorruptedStorage_IsNotReplacedSilently()
    {
        var storage = new MemoryProtectedKeyStorage { Bytes = [1, 2, 3] };
        var provider = new WindowsDeviceIdentityKeyProvider(storage, new PassThroughProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPublicKeyAsync());
    }

    [Fact]
    public async Task TlsCertificate_UsesStableIdentityPublicKey()
    {
        var storage = new MemoryProtectedKeyStorage();
        var protector = new PassThroughProtector();
        var first = new WindowsDeviceIdentityKeyProvider(storage, protector);

        var certificate = await first.GetServerCertificateAsync();
        using var certificateKey = certificate.GetECDsaPublicKey();
        var certificateSpki = certificateKey!.ExportSubjectPublicKeyInfo();

        Assert.Equal(await first.GetPublicKeyAsync(), certificateSpki);
        Assert.Equal(SecurityEncoding.Fingerprint(certificateSpki), await first.GetServerSpkiFingerprintAsync());

        var restarted = new WindowsDeviceIdentityKeyProvider(storage, protector);
        Assert.Equal(await first.GetServerSpkiFingerprintAsync(), await restarted.GetServerSpkiFingerprintAsync());
    }

    [Fact]
    public async Task IdentityKey_PersistsInFileStorageAcrossProcessLikeRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"devicesync-identity-{Guid.NewGuid():N}");
        var keyPath = Path.Combine(directory, "identity-key.bin");

        try
        {
            var first = new WindowsDeviceIdentityKeyProvider(
                new FileProtectedKeyStorage(keyPath),
                new PassThroughProtector());
            var firstIdentityFingerprint = await first.GetPublicKeyFingerprintAsync();
            var firstTlsFingerprint = await first.GetServerSpkiFingerprintAsync();

            var restarted = new WindowsDeviceIdentityKeyProvider(
                new FileProtectedKeyStorage(keyPath),
                new PassThroughProtector());

            Assert.True(File.Exists(keyPath));
            Assert.Equal(firstIdentityFingerprint, await restarted.GetPublicKeyFingerprintAsync());
            Assert.Equal(firstTlsFingerprint, await restarted.GetServerSpkiFingerprintAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CorruptedFileStorage_RequiresExplicitRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"devicesync-identity-{Guid.NewGuid():N}");
        var keyPath = Path.Combine(directory, "identity-key.bin");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(keyPath, [1, 2, 3]);
            var provider = new WindowsDeviceIdentityKeyProvider(
                new FileProtectedKeyStorage(keyPath),
                new PassThroughProtector());

            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetServerSpkiFingerprintAsync());
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(keyPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

internal sealed class MemoryProtectedKeyStorage : IProtectedKeyStorage
{
    public byte[]? Bytes { get; set; }
    public Task<byte[]?> ReadProtectedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Bytes);
    public Task WriteProtectedAtomicAsync(byte[] protectedBytes, CancellationToken cancellationToken = default)
    {
        Bytes = protectedBytes.ToArray();
        return Task.CompletedTask;
    }
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        Bytes = null;
        return Task.CompletedTask;
    }
}

internal sealed class PassThroughProtector : IDataProtector
{
    public byte[] Protect(byte[] plainBytes) => plainBytes.ToArray();
    public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
}
