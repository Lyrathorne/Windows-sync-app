using DeviceSync.Protocol;
using System.Text.Json;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class ProtocolNegotiationTests
{
    [Theory]
    [InlineData(1, null, null, 1)]
    [InlineData(1, 1, 2, 1)]
    [InlineData(2, 2, 3, null)]
    [InlineData(1, 2, 1, null)]
    public void Negotiation_SelectsHighestCommonVersion(
        int legacyVersion,
        int? remoteMin,
        int? remoteMax,
        int? expected)
    {
        Assert.Equal(expected, ProtocolVersionNegotiator.Negotiate(legacyVersion, remoteMin, remoteMax));
    }

    [Fact]
    public void SharedVector_IgnoresFutureFieldsAndPreservesLimits()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "negotiation", "connection-hello-v1.json");
        var message = ProtocolSerializer.Deserialize(File.ReadAllText(path));
        var payload = ProtocolSerializer.DecodePayload<ConnectionHelloPayload>(message.Payload);

        Assert.Equal("android-vector", message.OriginDeviceId);
        Assert.Equal(1, payload.ProtocolMin);
        Assert.Equal(2, payload.ProtocolMax);
        Assert.Equal(1_048_580, payload.MaxFrameBytes);
        Assert.Equal(983_040, payload.MaxPayloadBytes);
        Assert.Equal(1, ProtocolVersionNegotiator.Negotiate(payload.ProtocolVersion, payload.ProtocolMin, payload.ProtocolMax));
    }

    [Fact]
    public void CapabilityIntersection_AdvertisesImplementedMediaCatalog()
    {
        var result = CapabilityNegotiator.Intersect([
            SupportedCapabilities.ClipboardV1,
            SupportedCapabilities.TransportLanTlsV1,
            SupportedCapabilities.MediaCatalogV1,
        ]);

        Assert.Contains(SupportedCapabilities.ClipboardV1, result);
        Assert.Contains(SupportedCapabilities.TransportLanTlsV1, result);
        Assert.Contains(SupportedCapabilities.MediaCatalogV1, result);
        CapabilityNegotiator.Require(result, SupportedCapabilities.MediaCatalogV1);
    }

    [Fact]
    public void Serializer_RejectsPayloadAboveDocumentedLimit()
    {
        using var document = JsonDocument.Parse($"{{\"data\":\"{new string('x', ProtocolConstants.MaxJsonPayloadSize)}\"}}");
        var message = TestMessages.AndroidHello() with { Payload = document.RootElement.Clone() };

        var error = Assert.Throws<ProtocolException>(() => ProtocolSerializer.Serialize(message));
        Assert.Equal(ProtocolErrorCodes.PayloadTooLarge, error.Message);
    }
}
