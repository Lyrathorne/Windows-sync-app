using System.Net;
using System.Net.NetworkInformation;
using DeviceSync.Application;
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

    [Fact]
    public void CandidateEndpoints_ClassifyUsbHotspotAndExcludeVpn()
    {
        var provider = Provider(
            Adapter("USB Ethernet", "Remote NDIS based Internet Sharing Device", NetworkInterfaceType.Ethernet, "192.168.42.2"),
            Adapter("Local Area Connection", "Microsoft Wi-Fi Direct Virtual Adapter", NetworkInterfaceType.Wireless80211, "192.168.137.1"),
            Adapter("VPN", "WireGuard Tunnel", NetworkInterfaceType.Ethernet, "10.8.0.2"));

        var endpoints = provider.GetCandidateEndpoints(54321);

        Assert.Contains(endpoints, endpoint => endpoint.Kind == DeviceTransportKind.UsbTethering);
        Assert.Contains(endpoints, endpoint => endpoint.Kind == DeviceTransportKind.Hotspot);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Address == "10.8.0.2");
        Assert.All(endpoints, endpoint => Assert.False(string.IsNullOrWhiteSpace(endpoint.InterfaceId)));
    }

    [Fact]
    public void ActiveVpnDefaultRoute_DoesNotReplacePhysicalWifiAddress()
    {
        var provider = Provider(
            Adapter("Throne TUN", "Wintun Userspace Tunnel", NetworkInterfaceType.Ethernet, "10.20.0.2", "10.20.0.1"),
            Adapter("Wi-Fi", "Intel Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, "192.168.31.77", "192.168.31.1"));

        Assert.Equal(["192.168.31.77"], provider.GetLocalIPv4Addresses());
        Assert.All(provider.GetCandidateEndpoints(54321), endpoint => Assert.Equal("192.168.31.77", endpoint.Address));
    }

    [Theory]
    [InlineData("OpenVPN TAP Adapter")]
    [InlineData("WireGuard Wintun Tunnel")]
    [InlineData("NordLynx VPN")]
    [InlineData("Throne TUN")]
    public void VpnOnlyInterface_IsNeverAdvertised(string description)
    {
        var provider = Provider(
            Adapter("VPN", description, NetworkInterfaceType.Ethernet, "10.8.0.2", "10.8.0.1"));

        Assert.Empty(provider.GetLocalIPv4Addresses());
        Assert.Empty(provider.GetCandidateEndpoints(54321));
    }

    [Fact]
    public void MultiplePhysicalAddresses_AreOrderedAndRetained()
    {
        var provider = Provider(
            Adapter("Ethernet", "Realtek PCIe", NetworkInterfaceType.Ethernet, "192.168.1.20", "192.168.1.1"),
            Adapter("Wi-Fi", "Intel Wi-Fi 6", NetworkInterfaceType.Wireless80211, "192.168.50.20", "192.168.50.1"));

        Assert.Equal(["192.168.1.20", "192.168.50.20"], provider.GetLocalIPv4Addresses());
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
