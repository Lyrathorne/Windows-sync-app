using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class WindowsDeviceIdentityKeyProvider : IDeviceIdentityKeyProvider, ITlsCertificateProvider
{
    private readonly IProtectedKeyStorage _storage;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ECDsa? _cachedKey;
    private X509Certificate2? _cachedCertificate;

    public WindowsDeviceIdentityKeyProvider(IProtectedKeyStorage storage, IDataProtector protector)
    {
        _storage = storage;
        _protector = protector;
    }

    public async Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        return key.ExportSubjectPublicKeyInfo();
    }

    public async Task<string> GetPublicKeyFingerprintAsync(CancellationToken cancellationToken = default)
    {
        return SecurityEncoding.Fingerprint(await GetPublicKeyAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        return key.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public async Task<X509Certificate2> GetServerCertificateAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCertificate is not null)
        {
            return _cachedCertificate;
        }

        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        var request = new CertificateRequest(
            "CN=DeviceSync",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var ephemeralCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        var certificate = new X509Certificate2(
            ephemeralCertificate.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        _cachedCertificate = certificate;
        return certificate;
    }

    public async Task<string> GetServerSpkiFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var certificate = await GetServerCertificateAsync(cancellationToken).ConfigureAwait(false);
        using var publicKey = certificate.GetECDsaPublicKey()
            ?? throw new CryptographicException("TLS certificate does not contain an ECDSA public key.");
        return SecurityEncoding.Fingerprint(publicKey.ExportSubjectPublicKeyInfo());
    }

    public async Task ResetIdentityAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cachedKey?.Dispose();
            _cachedKey = null;
            _cachedCertificate?.Dispose();
            _cachedCertificate = null;
            await _storage.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ECDsa> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            var protectedBytes = await _storage.ReadProtectedAsync(cancellationToken).ConfigureAwait(false);
            if (protectedBytes is not null)
            {
                try
                {
                    var privateKey = _protector.Unprotect(protectedBytes);
                    var loaded = ECDsa.Create();
                    loaded.ImportPkcs8PrivateKey(privateKey, out _);
                    _cachedKey = loaded;
                    return loaded;
                }
                catch (Exception error) when (error is CryptographicException or IOException)
                {
                    throw new InvalidOperationException("Windows identity key is corrupted and was not replaced automatically.", error);
                }
            }

            var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pkcs8 = created.ExportPkcs8PrivateKey();
            await _storage.WriteProtectedAtomicAsync(_protector.Protect(pkcs8), cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(pkcs8);
            _cachedKey = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }
}
