using Microsoft.Win32;

namespace DeviceSync.App;

public sealed class WindowsStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeviceSync";
    private const string SettingsKey = @"Software\DeviceSync";
    public bool ClipboardEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(SettingsKey); return key?.GetValue("ClipboardEnabled") is 1; }
        set { using var key = Registry.CurrentUser.CreateSubKey(SettingsKey); key.SetValue("ClipboardEnabled", value ? 1 : 0, RegistryValueKind.DWord); }
    }

    public IReadOnlySet<string> ClipboardAllowedDeviceIds
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return (key?.GetValue("ClipboardAllowedDeviceIds") as string[] ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    public bool IsClipboardAllowedForDevice(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) && ClipboardAllowedDeviceIds.Contains(deviceId);

    public void SetClipboardAllowedForDevice(string deviceId, bool allowed)
    {
        var updated = ClipboardAllowedDeviceIds.ToHashSet(StringComparer.Ordinal);
        if (allowed) updated.Add(deviceId); else updated.Remove(deviceId);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("ClipboardAllowedDeviceIds", updated.OrderBy(value => value).ToArray(), RegistryValueKind.MultiString);
    }
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled) { key.DeleteValue(ValueName, false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable.");
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }
}
