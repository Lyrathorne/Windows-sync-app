using System.Text.Json;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class JsonFolderSyncRootStore : IFolderSyncRootStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeviceSync", "folder-sync-root.json");

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return (await JsonSerializer.DeserializeAsync<RootRecord>(stream, cancellationToken: cancellationToken).ConfigureAwait(false))?.RootPath;
    }

    public async Task SaveAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, new RootRecord(rootPath), cancellationToken: cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, true);
    }

    private sealed record RootRecord(string RootPath);
}
