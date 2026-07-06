using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeviceSync.Application;

public static class SecurityEncoding
{
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    public static string Fingerprint(ReadOnlySpan<byte> publicKeySpkiDer)
    {
        return Base64UrlEncode(SHA256.HashData(publicKeySpkiDer));
    }

    public static string FingerprintDisplay(string fingerprintBase64Url)
    {
        return string.Join(':', Base64UrlDecode(fingerprintBase64Url).Select(b => b.ToString("X2")));
    }

    public static byte[] HmacSha256(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> data)
    {
        return HMACSHA256.HashData(secret, data);
    }

    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static string VerificationCode(params string[] fields)
    {
        var digest = SHA256.HashData(TranscriptBuilder.Build(fields));
        var value = BinaryPrimitives.ReadInt32BigEndian(digest.AsSpan(0, 4)) & int.MaxValue;
        return (value % 1_000_000).ToString("D6");
    }
}

public static class TranscriptBuilder
{
    public static byte[] PairingRequest(
        string sessionId,
        string windowsDeviceId,
        string androidDeviceId,
        string windowsFingerprint,
        string androidFingerprint,
        string androidNonce)
    {
        return Build(
            "DeviceSyncPairingV1",
            sessionId,
            windowsDeviceId,
            androidDeviceId,
            windowsFingerprint,
            androidFingerprint,
            androidNonce);
    }

    public static byte[] PairingChallenge(
        string sessionId,
        string windowsDeviceId,
        string androidDeviceId,
        string windowsFingerprint,
        string androidFingerprint,
        string androidNonce,
        string windowsNonce)
    {
        return Build(
            "DeviceSyncPairingChallengeV1",
            sessionId,
            windowsDeviceId,
            androidDeviceId,
            windowsFingerprint,
            androidFingerprint,
            androidNonce,
            windowsNonce);
    }

    public static byte[] PairingConfirmation(
        string sessionId,
        string windowsDeviceId,
        string androidDeviceId,
        string windowsFingerprint,
        string androidFingerprint,
        string androidNonce,
        string windowsNonce,
        string verificationCode)
    {
        return Build(
            "DeviceSyncPairingConfirmV1",
            sessionId,
            windowsDeviceId,
            androidDeviceId,
            windowsFingerprint,
            androidFingerprint,
            androidNonce,
            windowsNonce,
            verificationCode);
    }

    public static byte[] PairingAccepted(
        string sessionId,
        string windowsDeviceId,
        string androidDeviceId,
        string windowsFingerprint,
        string androidFingerprint,
        string androidNonce,
        string windowsNonce,
        string verificationCode,
        string pairedAtUtc,
        IReadOnlyList<string> permissions)
    {
        return Build(
            "DeviceSyncPairingAcceptedV1",
            sessionId,
            windowsDeviceId,
            androidDeviceId,
            windowsFingerprint,
            androidFingerprint,
            androidNonce,
            windowsNonce,
            verificationCode,
            pairedAtUtc,
            string.Join(',', permissions));
    }

    public static byte[] SessionAuth(
        int protocolVersion,
        string androidDeviceId,
        string windowsDeviceId,
        string androidFingerprint,
        string windowsFingerprint,
        string clientNonce,
        string serverNonce,
        string helloMessageId)
    {
        return Build(
            "DeviceSyncSessionAuthV1",
            protocolVersion.ToString(),
            androidDeviceId,
            windowsDeviceId,
            androidFingerprint,
            windowsFingerprint,
            clientNonce,
            serverNonce,
            helloMessageId);
    }

    public static byte[] Build(params string[] fields)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }
}
