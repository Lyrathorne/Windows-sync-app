using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeviceSync.Application;
using Microsoft.Win32;

namespace DeviceSync.App;

public sealed class IncomingFileAutomationSettings : IIncomingFilePolicyStore, IIncomingFileNetworkContext
{
    private const string SettingsKey = @"Software\DeviceSync";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool AutomaticReceiveEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(SettingsKey); return key?.GetValue("AutomaticFileReceiveEnabled") is 1; }
        set { using var key = Registry.CurrentUser.CreateSubKey(SettingsKey); key.SetValue("AutomaticFileReceiveEnabled", value ? 1 : 0, RegistryValueKind.DWord); }
    }

    public bool IsCurrentNetworkTrusted
    {
        get
        {
            var fingerprint = CurrentNetworkFingerprint();
            return fingerprint is not null && TrustedNetworkFingerprints.Contains(fingerprint);
        }
    }

    public IncomingFileAutomationPolicy GetPolicy(string deviceId)
    {
        var policies = ReadPolicies();
        return policies.TryGetValue(deviceId, out var policy) ? policy : new();
    }

    public void SetPolicy(string deviceId, IncomingFileAutomationPolicy policy)
    {
        var policies = ReadPolicies();
        policies[deviceId] = policy with
        {
            AutomaticLimitBytes = Math.Clamp(policy.AutomaticLimitBytes, 0, IncomingFileTransferManager.MaximumFileSize),
        };
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("IncomingFilePolicies", JsonSerializer.Serialize(policies, JsonOptions), RegistryValueKind.String);
    }

    public bool SetCurrentNetworkTrusted(bool trusted)
    {
        var fingerprint = CurrentNetworkFingerprint();
        if (fingerprint is null) return false;
        var updated = TrustedNetworkFingerprints.ToHashSet(StringComparer.Ordinal);
        if (trusted) updated.Add(fingerprint); else updated.Remove(fingerprint);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("TrustedNetworkFingerprints", updated.OrderBy(value => value).ToArray(), RegistryValueKind.MultiString);
        return true;
    }

    private IReadOnlySet<string> TrustedNetworkFingerprints
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return (key?.GetValue("TrustedNetworkFingerprints") as string[] ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, IncomingFileAutomationPolicy> ReadPolicies()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var json = key?.GetValue("IncomingFilePolicies") as string;
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, IncomingFileAutomationPolicy>>(json, JsonOptions)
                ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static string? CurrentNetworkFingerprint()
    {
        var parts = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(adapter => new
            {
                adapter.Id,
                Gateways = adapter.GetIPProperties().GatewayAddresses
                    .Select(gateway => gateway.Address.ToString())
                    .Where(address => address is not "0.0.0.0" and not "::")
                    .OrderBy(address => address, StringComparer.Ordinal)
                    .ToArray(),
            })
            .Where(adapter => adapter.Gateways.Length > 0)
            .Select(adapter => $"{adapter.Id}|{string.Join(',', adapter.Gateways)}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (parts.Length == 0) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', parts))));
    }
}
