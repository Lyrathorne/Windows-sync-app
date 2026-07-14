using System.Text.Json;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class JsonIncomingTransferCheckpointStore : IIncomingTransferCheckpointStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonIncomingTransferCheckpointStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceSync", "transfers", "incoming"))
    {
    }

    public JsonIncomingTransferCheckpointStore(string directory) => _directory = directory;

    public async Task SaveAsync(IncomingTransferCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(checkpoint.TransferId);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(checkpoint, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<IncomingTransferCheckpoint?> LoadAsync(string transferId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(transferId);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<IncomingTransferCheckpoint>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string transferId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (File.Exists(PathFor(transferId))) File.Delete(PathFor(transferId)); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<IncomingTransferCheckpoint>> GetExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return [];
            var result = new List<IncomingTransferCheckpoint>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var checkpoint = JsonSerializer.Deserialize<IncomingTransferCheckpoint>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                    if (checkpoint is not null && checkpoint.LastActivityAtUtc < cutoffUtc) result.Add(checkpoint);
                }
                catch (JsonException) { File.Delete(path); }
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private string PathFor(string transferId) => Path.Combine(_directory, transferId + ".json");
}
