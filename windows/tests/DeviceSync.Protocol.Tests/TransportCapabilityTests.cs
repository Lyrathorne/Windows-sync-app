using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class TransportCapabilityTests
{
    [Fact]
    public void CurrentBuild_AdvertisesAllLocalTransportCapabilities()
    {
        Assert.Contains(SupportedCapabilities.TransportLanTlsV1, SupportedCapabilities.Values);
        Assert.Contains(SupportedCapabilities.TransportHotspotV1, SupportedCapabilities.Values);
        Assert.Contains(SupportedCapabilities.TransportUsbV1, SupportedCapabilities.Values);
        Assert.Contains(SupportedCapabilities.TransportBluetoothV1, SupportedCapabilities.Values);
    }
}
