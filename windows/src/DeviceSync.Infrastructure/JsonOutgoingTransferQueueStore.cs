using System.Text.Json;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class JsonOutgoingTransferQueueStore : IOutgoingTransferQueueStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonOutgoingTransferQueueStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceSync", "transfers", "outgoing-queue.json")) { }
    public JsonOutgoingTransferQueueStore(string path) => _path = path;

    public async Task<IReadOnlyList<OutgoingTransferQueueItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task UpsertAsync(OutgoingTransferQueueItem item, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await ReadLockedAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = items.FindIndex(candidate => candidate.TransferId == item.TransferId);
            if (index >= 0) items[index] = item; else items.Add(item);
            await WriteLockedAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string transferId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await ReadLockedAsync(cancellationToken).ConfigureAwait(false)).Where(item => item.TransferId != transferId).ToList();
            await WriteLockedAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<OutgoingTransferQueueItem>> ReadLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        return JsonSerializer.Deserialize<List<OutgoingTransferQueueItem>>(
            await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false), Options) ?? [];
    }

    private async Task WriteLockedAsync(IReadOnlyList<OutgoingTransferQueueItem> items, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(items, Options), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, true);
    }
}
