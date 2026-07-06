using System.Text.Json;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class FileTrustedDeviceRepository : ITrustedDeviceRepository
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileTrustedDeviceRepository()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceSync",
            "Security",
            "trusted-devices.json"))
    {
    }

    public FileTrustedDeviceRepository(string path)
    {
        _path = path;
    }

    public async Task<IReadOnlyList<TrustedDevice>> GetTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(device => device.RevokedAtUtc is null && device.TrustStatus != TrustStatuses.Revoked)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrustedDevice?> GetTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(device => device.DeviceId == deviceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveTrustedDeviceAsync(TrustedDevice device, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(existing => existing.DeviceId != device.DeviceId)
                .Append(device)
                .ToList();
            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateLastVerifiedAtAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false))
                .Select(device => device.DeviceId == deviceId ? device with { LastVerifiedAtUtc = timestamp } : device)
                .ToList();
            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ActivateTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false))
                .Select(device => device.DeviceId == deviceId ? device with { TrustStatus = TrustStatuses.Active } : device)
                .ToList();
            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RevokeAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false))
                .Select(device => device.DeviceId == deviceId ? device with { RevokedAtUtc = timestamp, TrustStatus = TrustStatuses.Revoked } : device)
                .ToList();
            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(device => device.DeviceId != deviceId)
                .ToList();
            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<TrustedDevice>> ReadAllCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<TrustedDevice>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    private async Task WriteAllCoreAsync(List<TrustedDevice> devices, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_path}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, devices, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, _path, overwrite: true);
    }
}
