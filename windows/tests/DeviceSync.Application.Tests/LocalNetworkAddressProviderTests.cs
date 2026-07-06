using System.Net;
using System.Net.NetworkInformation;
using DeviceSync.Infrastructure;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class LocalNetworkAddressProviderTests
{
    [Fact]
    public void WifiOrEthernet_IsSelectedBeforeVirtualAdapter()
    {
        var provider = Provider(
            Adapter("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, "172.20.10.2", "172.20.10.1"),
            Adapter("Wi-Fi", "Intel Wi-Fi 6", NetworkInterfaceType.Wireless80211, "192.168.1.45", "192.168.1.1"));

        var addresses = provider.GetLocalIPv4Addresses();

        Assert.Equal("192.168.1.45", addresses[0]);
        Assert.DoesNotContain("172.20.10.2", addresses);
    }

    [Fact]
    public void AdapterWithoutGateway_HasLowerPriority()
    {
        var provider = Provider(
            Adapter("Ethernet no gateway", "Realtek Ethernet", NetworkInterfaceType.Ethernet, "192.168.56.10"),
            Adapter("Ethernet", "Realtek Ethernet", NetworkInterfaceType.Ethernet, "192.168.1.45", "192.168.1.1"));

        var addresses = provider.GetLocalIPv4Addresses();

        Assert.Equal("192.168.1.45", addresses[0]);
    }

    [Fact]
    public void Loopback_IsNeverSelected()
    {
        var provider = Provider(
            Adapter("Loopback", "Loopback", NetworkInterfaceType.Loopback, "127.0.0.1"));

        Assert.Empty(provider.GetLocalIPv4Addresses());
        Assert.Null(provider.GetPrimaryLocalIPv4Address());
    }

    private static LocalNetworkAddressProvider Provider(params LocalNetworkInterfaceSnapshot[] adapters)
    {
        return new LocalNetworkAddressProvider(() => adapters);
    }

    private static LocalNetworkInterfaceSnapshot Adapter(
        string name,
        string description,
        NetworkInterfaceType type,
        string address,
        string? gateway = null)
    {
        return new LocalNetworkInterfaceSnapshot(
            name,
            description,
            type,
            OperationalStatus.Up,
            [IPAddress.Parse(address)],
            gateway is null ? [] : [IPAddress.Parse(gateway)]);
    }
}
