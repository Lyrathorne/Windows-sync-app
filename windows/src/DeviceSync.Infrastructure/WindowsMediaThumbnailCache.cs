using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class WindowsMediaThumbnailCache : IMediaThumbnailCache
{
    private readonly long _maximumBytes;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsMediaThumbnailCache(string? directory = null, long maximumBytes = 64L * 1024 * 1024)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceSync",
            "ThumbnailCache");
        _maximumBytes = Math.Max(1, maximumBytes);
    }

    public async Task<CachedMediaThumbnail?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (entry?.Data is null || entry.Data.Length > 256 * 1024)
            {
                File.Delete(path);
                return null;
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return new(entry.MimeType, entry.Width, entry.Height, entry.Data);
        }
        catch (JsonException)
        {
            File.Delete(path);
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task PutAsync(string key, CachedMediaThumbnail thumbnail, CancellationToken cancellationToken = default)
    {
        if (thumbnail.Data.Length > 256 * 1024) return;
        Directory.CreateDirectory(_directory);
        var path = GetPath(key);
        var temporary = path + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new CacheEntry(thumbnail.MimeType, thumbnail.Width, thumbnail.Height, thumbnail.Data),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
            EvictLocked();
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(file);
            }
        }
        finally { _gate.Release(); }
    }

    private void EvictLocked()
    {
        var files = Directory.EnumerateFiles(_directory, "*.json")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastAccessTimeUtc)
            .ToArray();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= _maximumBytes) break;
            total -= file.Length;
            file.Delete();
        }
    }

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, $"{hash}.json");
    }

    private sealed record CacheEntry(string MimeType, int Width, int Height, byte[] Data);
}
