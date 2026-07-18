using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed record LocalNetworkInterfaceSnapshot(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    IReadOnlyList<IPAddress> UnicastAddresses,
    IReadOnlyList<IPAddress> GatewayAddresses);

public sealed class LocalNetworkAddressProvider : ILocalNetworkAddressProvider
{
    private readonly Func<IReadOnlyList<LocalNetworkInterfaceSnapshot>> _interfaces;

    public LocalNetworkAddressProvider()
        : this(GetSystemInterfaces)
    {
    }

    public LocalNetworkAddressProvider(Func<IReadOnlyList<LocalNetworkInterfaceSnapshot>> interfaces)
    {
        _interfaces = interfaces;
    }

    public IReadOnlyList<string> GetLocalIPv4Addresses()
    {
        var candidates = _interfaces()
            .Where(adapter => adapter.Status == OperationalStatus.Up)
            .Where(adapter => adapter.Type != NetworkInterfaceType.Loopback && adapter.Type != NetworkInterfaceType.Tunnel)
            .Where(adapter => !IsVpnAdapter(adapter))
            .Where(adapter => !IsUnsupportedVirtualAdapter(adapter))
            .SelectMany(adapter => adapter.UnicastAddresses
                .Where(IsUsablePrivateIPv4)
                .Select(address => new AddressCandidate(adapter, address)))
            .ToList();

        return candidates
            .OrderBy(candidate => Score(candidate.Adapter))
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .Select(candidate => candidate.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public string? GetPrimaryLocalIPv4Address()
    {
        return GetLocalIPv4Addresses().FirstOrDefault();
    }

    public IReadOnlyList<DeviceTransportEndpoint> GetCandidateEndpoints(int port)
    {
        return _interfaces()
            .Where(adapter => adapter.Status == OperationalStatus.Up)
            .Where(adapter => adapter.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(adapter => !IsVpnAdapter(adapter))
            .Where(adapter => !IsUnsupportedVirtualAdapter(adapter))
            .SelectMany(adapter => adapter.UnicastAddresses
                .Where(IsUsableLocalAddress)
                .Select(address => new DeviceTransportEndpoint(
                    ClassifyTransport(adapter, address),
                    address.ToString(),
                    port,
                    InterfaceId: PrivacySafeInterfaceId(adapter),
                    IsRemembered: false)))
            .OrderByDescending(endpoint => DeviceTransportProfile.For(endpoint.Kind).Priority)
            .ThenBy(endpoint => endpoint.Address, StringComparer.Ordinal)
            .DistinctBy(endpoint => (endpoint.Kind, endpoint.Address, endpoint.Port))
            .ToArray();
    }

    private static IReadOnlyList<LocalNetworkInterfaceSnapshot> GetSystemInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Select(adapter =>
            {
                var properties = adapter.GetIPProperties();
                return new LocalNetworkInterfaceSnapshot(
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType,
                    adapter.OperationalStatus,
                    properties.UnicastAddresses.Select(address => address.Address).ToList(),
                    properties.GatewayAddresses.Select(address => address.Address).ToList());
            })
            .ToList();
    }

    private static int Score(LocalNetworkInterfaceSnapshot adapter)
    {
        var score = 0;
        if (!IsPreferredLanType(adapter.Type)) score += 100;
        if (!HasUsableGateway(adapter)) score += 20;
        if (IsKnownVirtualAdapter(adapter)) score += 200;
        return score;
    }

    private static bool IsPreferredLanType(NetworkInterfaceType type)
    {
        return type is NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet;
    }

    private static bool HasUsableGateway(LocalNetworkInterfaceSnapshot adapter)
    {
        return adapter.GatewayAddresses.Any(address =>
            address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(address) &&
            !address.Equals(IPAddress.Any));
    }

    private static bool IsUsablePrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsUsableLocalAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsUsablePrivateIPv4(address);
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            address.IsIPv6LinkLocal &&
            !IPAddress.IsLoopback(address);
    }

    private static DeviceTransportKind ClassifyTransport(
        LocalNetworkInterfaceSnapshot adapter,
        IPAddress address)
    {
        var text = $"{adapter.Name} {adapter.Description}";
        if (text.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) ||
            address.ToString().StartsWith("192.168.42.", StringComparison.Ordinal))
            return DeviceTransportKind.UsbTethering;
        if (text.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Mobile Hotspot", StringComparison.OrdinalIgnoreCase) ||
            address.ToString().StartsWith("192.168.137.", StringComparison.Ordinal))
            return DeviceTransportKind.Hotspot;
        return DeviceTransportKind.Lan;
    }

    private static bool IsVpnAdapter(LocalNetworkInterfaceSnapshot adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}";
        return text.Contains("VPN", StringComparison.OrdinalIgnoreCase)
            || text.Contains(" TUN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP Adapter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ProtonVPN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NordLynx", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Mullvad", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Hamachi", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Throne", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrivacySafeInterfaceId(LocalNetworkInterfaceSnapshot adapter)
    {
        var value = $"{adapter.Type}|{adapter.Name}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 6));
    }

    private static bool IsKnownVirtualAdapter(LocalNetworkInterfaceSnapshot adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}";
        return ContainsKnownVirtualMarker(text);
    }

    private static bool IsUnsupportedVirtualAdapter(LocalNetworkInterfaceSnapshot adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}";
        if (text.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Mobile Hotspot", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ContainsKnownVirtualMarker(text);
    }

    private static bool ContainsKnownVirtualMarker(string text)
    {
        return text.Contains("WSL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VPN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AddressCandidate(LocalNetworkInterfaceSnapshot Adapter, IPAddress Address);
}
