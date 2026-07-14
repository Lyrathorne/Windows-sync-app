using System.Security.Cryptography;
using DeviceSync.Application;
using DeviceSync.Protocol;

namespace DeviceSync.Infrastructure;

public sealed class WindowsFolderManifestBuilder : IFolderManifestBuilder
{
    public async Task<FolderManifestPayload> BuildAsync(string rootPath, string syncId, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var entries = new List<FolderManifestEntryPayload>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            FolderSyncPlanner.Normalize(relative);
            var info = new FileInfo(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            entries.Add(new FolderManifestEntryPayload
            {
                RelativePath = relative,
                SizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc.ToString("O"),
                Sha256 = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            });
        }
        return new FolderManifestPayload
        {
            SyncId = syncId,
            RootId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant(),
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Entries = entries,
        };
    }
}
