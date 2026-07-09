using System.Net;
using System.Text.Json;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class QrAndMdnsAddressTests
{
    [Fact]
    public void QrGenerator_ReturnsNonEmptyPngWithSufficientSize()
    {
        var generator = new QrCodeGenerator();
        var payload = JsonSerializer.Serialize(SamplePairingPayload(), JsonOptions);

        var png = generator.GeneratePng(payload, 4);

        Assert.True(png.Length > 0);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        var (width, height) = ReadPngSize(png);
        Assert.True(width >= 768, $"Expected QR width >= 768, got {width}.");
        Assert.True(height >= 768, $"Expected QR height >= 768, got {height}.");
    }

    [Fact]
    public void MdnsARecord_UsesAdvertisedAddress()
    {
        var service = Service("192.168.1.45");

        var packet = SimpleMdnsServiceDiscoveryPublisher.BuildResponseForTest(
            service,
            "Gleb-PC.local",
            IPAddress.Parse(service.AdvertisedAddress!));

        Assert.True(ContainsSequence(packet, IPAddress.Parse("192.168.1.45").GetAddressBytes()));
        Assert.False(ContainsSequence(packet, IPAddress.Loopback.GetAddressBytes()));
    }

    [Fact]
    public async Task PairingQr_FirstHostMatchesAdvertisedAddress()
    {
        var advertisedAddress = "192.168.1.45";
        var manager = new PairingSessionManager(new FakeIdentityProvider("windows-test"), new FakeKeyProvider());

        var qr = await manager.StartPairingAsync(54321, [advertisedAddress, "192.168.1.46"]);

        Assert.Equal(advertisedAddress, qr.HostAddresses[0]);
    }

    [Fact]
    public async Task PairingQr_EmptyAddressList_DoesNotFallbackToLoopback()
    {
        var manager = new PairingSessionManager(new FakeIdentityProvider("windows-test"), new FakeKeyProvider());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartPairingAsync(54321, []));

        Assert.Equal("Не найден адрес локальной сети", error.Message);
        Assert.Null(manager.CurrentQrPayload);
    }

    private static PublishedService Service(string advertisedAddress) => new()
    {
        InstanceName = "Gleb-PC",
        ServiceType = "_devicesync._tcp",
        Port = 54321,
        AdvertisedAddress = advertisedAddress,
        TxtRecords = new Dictionary<string, string>
        {
            ["deviceId"] = "windows-fixed",
            ["deviceName"] = "Gleb-PC",
            ["deviceType"] = "windows",
            ["protocolMin"] = ProtocolConstants.ProtocolVersion.ToString(),
            ["protocolMax"] = ProtocolConstants.ProtocolVersion.ToString(),
            ["pairingAvailable"] = "true",
        },
    };

    private static PairingQrPayload SamplePairingPayload() => new()
    {
        Format = "devicesync-pairing",
        Version = 1,
        SessionId = $"pair-{Guid.NewGuid()}",
        PairingSecret = SecurityEncoding.Base64UrlEncode(new byte[32]),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"),
        HostAddresses = ["192.168.1.45"],
        Port = 54321,
        WindowsDeviceId = "windows-test",
        WindowsDeviceName = "Gleb-PC",
        WindowsIdentityPublicKey = SecurityEncoding.Base64UrlEncode(new byte[180]),
        WindowsIdentityFingerprint = SecurityEncoding.Fingerprint(new byte[180]),
        ProtocolMin = ProtocolConstants.ProtocolVersion,
        ProtocolMax = ProtocolConstants.ProtocolVersion,
    };

    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        return (ReadBigEndianInt32(png, 16), ReadBigEndianInt32(png, 20));
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) |
            (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) |
            bytes[offset + 3];
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
