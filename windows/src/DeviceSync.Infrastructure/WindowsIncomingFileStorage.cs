using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class WindowsIncomingFileStorage : IIncomingFileStorage
{
    public string DefaultReceiveDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "DeviceSync");

    public long GetAvailableBytes(string directoryPath)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException("Destination has no filesystem root.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public FileTransferReservation Reserve(string directoryPath, string safeFileName, string transferId)
        => Reserve(directoryPath, safeFileName, transferId, replaceExisting: false);

    public FileTransferReservation Reserve(string directoryPath, string safeFileName, string transferId, bool replaceExisting)
    {
        var directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        var destination = replaceExisting ? Path.Combine(directory, safeFileName) : UniqueDestination(directory, safeFileName);
        var temporaryPath = Path.Combine(directory, $".devicesync-{transferId}.part");
        if (File.Exists(temporaryPath))
        {
            throw new IOException("A temporary file already exists for this transfer ID.");
        }

        return new FileTransferReservation(temporaryPath, destination, replaceExisting);
    }

    public Stream OpenWrite(string temporaryPath)
    {
        return new FileStream(temporaryPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 65_536,
        });
    }

    public Stream OpenResume(string temporaryPath, long offset)
    {
        var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65_536, FileOptions.Asynchronous);
        if (stream.Length != offset)
        {
            stream.Dispose();
            throw new IOException("Partial file length does not match the saved checkpoint.");
        }
        stream.Position = offset;
        return stream;
    }

    public Stream OpenReadPartial(string temporaryPath)
        => new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public Task<string> CommitAsync(FileTransferReservation reservation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = reservation.DestinationPath;
        if (File.Exists(destination) && !reservation.ReplaceExisting)
        {
            destination = UniqueDestination(Path.GetDirectoryName(destination)!, Path.GetFileName(destination));
        }

        File.Move(reservation.TemporaryPath, destination, overwrite: reservation.ReplaceExisting);
        return Task.FromResult(destination);
    }

    public Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static string UniqueDestination(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("No unique destination filename is available.");
    }
}
