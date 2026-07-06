using System.Text.Json;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class FileWindowsDeviceIdentityProvider : IWindowsDeviceIdentityProvider
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileWindowsDeviceIdentityProvider()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceSync", "settings.json"))
    {
    }

    public FileWindowsDeviceIdentityProvider(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public async Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await ReadSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(settings.WindowsDeviceId))
            {
                return settings.WindowsDeviceId;
            }

            var updated = settings with { WindowsDeviceId = $"windows-{Guid.NewGuid()}" };
            await SaveSettingsCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated.WindowsDeviceId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveSettingsCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> ReadSettingsCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(_settingsPath);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? new AppSettings();
    }

    private async Task SaveSettingsCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
