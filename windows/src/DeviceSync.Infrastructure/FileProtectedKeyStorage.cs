using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class FileProtectedKeyStorage : IProtectedKeyStorage
{
    private readonly string _path;

    public FileProtectedKeyStorage()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceSync",
            "Security",
            "identity-key.bin"))
    {
    }

    public FileProtectedKeyStorage(string path)
    {
        _path = path;
    }

    public async Task<byte[]?> ReadProtectedAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteProtectedAtomicAsync(byte[] protectedBytes, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_path}.tmp";
        await File.WriteAllBytesAsync(tempPath, protectedBytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _path, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
