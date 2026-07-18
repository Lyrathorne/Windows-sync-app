using DeviceSync.Application;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class TransportPolicyTests
{
    [Fact]
    public void Selector_PrefersUsbThenLanAndUsesBluetoothOnlyAsFallback()
    {
        var selector = new DeviceTransportSelector();
        var endpoints = new[]
        {
            new DeviceTransportEndpoint(DeviceTransportKind.BluetoothRfcomm, "AA:BB"),
            new DeviceTransportEndpoint(DeviceTransportKind.Lan, "192.168.1.8", 54321),
            new DeviceTransportEndpoint(DeviceTransportKind.UsbTethering, "192.168.42.1", 54321),
        };

        Assert.Equal(DeviceTransportKind.UsbTethering, selector.SelectBest(endpoints)!.Kind);
        Assert.Equal(DeviceTransportKind.BluetoothRfcomm, selector.SelectBest(
            endpoints,
            new Dictionary<DeviceTransportKind, DeviceTransportMetrics>
            {
                [DeviceTransportKind.UsbTethering] = new(DeviceTransportKind.UsbTethering, false),
                [DeviceTransportKind.Lan] = new(DeviceTransportKind.Lan, false),
                [DeviceTransportKind.BluetoothRfcomm] = new(DeviceTransportKind.BluetoothRfcomm, true),
            })!.Kind);
    }

    [Fact]
    public void BluetoothProfile_DisablesHeavyFeaturesAndLimitsFiles()
    {
        var profile = DeviceTransportProfile.For(DeviceTransportKind.BluetoothRfcomm);
        Assert.True(profile.IsSlow);
        Assert.Equal(2 * 1024 * 1024, profile.MaximumFileBytes);
        Assert.Contains("media-catalog-v1", profile.DisabledCapabilities);
        Assert.Contains("thumbnails-v1", profile.DisabledCapabilities);
    }

    [Fact]
    public void Deduplicator_RejectsSameMessageFromSameSender()
    {
        var deduplicator = new RecentMessageDeduplicator();
        var now = DateTimeOffset.UtcNow;
        Assert.True(deduplicator.TryAccept("phone", "message", now));
        Assert.False(deduplicator.TryAccept("phone", "message", now));
        Assert.True(deduplicator.TryAccept("other-phone", "message", now));
    }
}
