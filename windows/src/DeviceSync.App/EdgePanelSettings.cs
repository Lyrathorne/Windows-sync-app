using System.Text.Json;
using System.IO;
using DeviceSync.Application;

namespace DeviceSync.App;

public sealed record EdgePanelSettings
{
    public bool Enabled { get; init; } = true;
    public EdgePanelSide Side { get; init; } = EdgePanelSide.Right;
    public bool Expanded { get; init; }
    public int SelectedSection { get; init; }
    public int HoverDelayMilliseconds { get; init; } = 350;
    public string? MonitorDeviceName { get; init; }
    public bool HideRecentThumbnails { get; init; }
}

public interface IEdgePanelSettingsStore
{
    Task<EdgePanelSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(EdgePanelSettings settings, CancellationToken cancellationToken = default);
}

public sealed class JsonEdgePanelSettingsStore : IEdgePanelSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonEdgePanelSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceSync",
            "edge-panel.json"))
    {
    }

    public JsonEdgePanelSettingsStore(string path)
    {
        _path = path;
    }

    public async Task<EdgePanelSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new EdgePanelSettings();
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<EdgePanelSettings>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? new EdgePanelSettings();
            return settings with
            {
                SelectedSection = Math.Clamp(settings.SelectedSection, 0, 4),
                HoverDelayMilliseconds = Math.Clamp(settings.HoverDelayMilliseconds, 0, 2000),
            };
        }
        catch (JsonException)
        {
            return new EdgePanelSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(EdgePanelSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = $"{_path}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
