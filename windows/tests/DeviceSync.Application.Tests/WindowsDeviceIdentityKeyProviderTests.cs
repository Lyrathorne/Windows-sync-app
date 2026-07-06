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
    }

    [Fact]
    public async Task CorruptedStorage_IsNotReplacedSilently()
    {
        var storage = new MemoryProtectedKeyStorage { Bytes = [1, 2, 3] };
        var provider = new WindowsDeviceIdentityKeyProvider(storage, new PassThroughProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPublicKeyAsync());
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
