namespace DeviceSync.Application;

public enum OutgoingTransferQueueState { Queued, Sending, WaitingForRetry, Completed, Failed, Cancelled }

public sealed record OutgoingTransferQueueItem(
    string TransferId,
    string FilePath,
    string MimeType,
    string? Sha256,
    long AcknowledgedOffset,
    int NextChunkIndex,
    int AttemptCount,
    OutgoingTransferQueueState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastError = null,
    bool CanResume = false,
    string? FolderSyncId = null,
    string? RelativePath = null,
    bool ConflictCopy = false);

public interface IOutgoingTransferQueueStore
{
    Task<IReadOnlyList<OutgoingTransferQueueItem>> LoadAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(OutgoingTransferQueueItem item, CancellationToken cancellationToken = default);
    Task DeleteAsync(string transferId, CancellationToken cancellationToken = default);
}

public sealed class OutgoingTransferQueue : IAsyncDisposable
{
    private readonly OutgoingFileTransferManager _manager;
    private readonly IOutgoingTransferQueueStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<OutgoingTransferQueueItem> _items = [];
    private Task? _worker;
    private OutgoingTransferQueueItem? _active;

    public OutgoingTransferQueue(OutgoingFileTransferManager manager, IOutgoingTransferQueueStore store, TimeProvider? timeProvider = null)
    {
        _manager = manager;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _manager.Changed += OnTransferChanged;
    }

    public event EventHandler? Changed;
    public IReadOnlyList<OutgoingTransferQueueItem> Items { get { lock (_gate) return _items.ToArray(); } }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(persisted.Where(item => item.State is not OutgoingTransferQueueState.Completed and not OutgoingTransferQueueState.Cancelled));
        }
        _worker ??= Task.Run(() => WorkerAsync(_lifetime.Token));
        if (_items.Count > 0) _signal.Release();
    }

    public async Task<string> EnqueueAsync(
        string filePath,
        string mimeType = "application/octet-stream",
        CancellationToken cancellationToken = default,
        FolderTransferMetadata? folder = null)
    {
        var now = _timeProvider.GetUtcNow();
        var item = new OutgoingTransferQueueItem(
            Guid.NewGuid().ToString(), filePath, mimeType, null, 0, 0, 0,
            OutgoingTransferQueueState.Queued, now, now,
            FolderSyncId: folder?.SyncId, RelativePath: folder?.RelativePath, ConflictCopy: folder?.ConflictCopy ?? false);
        await _store.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
        lock (_gate) _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
        _signal.Release();
        return item.TransferId;
    }

    public async Task CancelAsync(string transferId, CancellationToken cancellationToken = default)
    {
        OutgoingTransferQueueItem? item;
        lock (_gate) item = _items.FirstOrDefault(candidate => candidate.TransferId == transferId);
        if (item is null) return;
        if (_active?.TransferId == transferId) await _manager.CancelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        item = item with { State = OutgoingTransferQueueState.Cancelled, UpdatedAtUtc = _timeProvider.GetUtcNow() };
        Replace(item);
        await _store.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (TryGetNext(out var item))
            {
                _active = item;
                item = item with { State = OutgoingTransferQueueState.Sending, UpdatedAtUtc = _timeProvider.GetUtcNow() };
                Replace(item);
                await _store.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                try
                {
                    var resume = item.CanResume && item.Sha256 is not null
                        ? new OutgoingTransferResumePoint(item.TransferId, item.Sha256, item.AcknowledgedOffset, item.NextChunkIndex)
                        : null;
                    var folder = item.FolderSyncId is not null && item.RelativePath is not null
                        ? new FolderTransferMetadata(item.FolderSyncId, item.RelativePath, item.ConflictCopy)
                        : null;
                    var result = await _manager.SendAsync(item.FilePath, item.MimeType, cancellationToken, resume, item.TransferId, folder).ConfigureAwait(false);
                    if (result.State == OutgoingFileTransferState.Rejected)
                        throw new InvalidOperationException(result.Error ?? "The receiver rejected the file.");
                    item = item with { State = OutgoingTransferQueueState.Completed, UpdatedAtUtc = _timeProvider.GetUtcNow(), LastError = null };
                    Replace(item);
                    await _store.DeleteAsync(item.TransferId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await ScheduleRetryAsync(item, "disconnected", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    await ScheduleRetryAsync(item, error.Message, cancellationToken).ConfigureAwait(false);
                }
                finally { _active = null; }
            }
        }
    }

    private async Task ScheduleRetryAsync(OutgoingTransferQueueItem item, string error, CancellationToken cancellationToken)
    {
        var transfer = _manager.ActiveTransfer;
        var attempts = item.AttemptCount + 1;
        item = item with
        {
            Sha256 = transfer?.Sha256 ?? item.Sha256,
            AcknowledgedOffset = transfer?.SentBytes ?? item.AcknowledgedOffset,
            NextChunkIndex = transfer?.NextChunkIndex ?? item.NextChunkIndex,
            AttemptCount = attempts,
            State = attempts >= 5 ? OutgoingTransferQueueState.Failed : OutgoingTransferQueueState.WaitingForRetry,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            LastError = error,
        };
        Replace(item);
        await _store.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
        if (attempts >= 5) return;
        await Task.Delay(TimeSpan.FromSeconds(1 << (attempts - 1)), _timeProvider, cancellationToken).ConfigureAwait(false);
        item = item with { State = OutgoingTransferQueueState.Queued, UpdatedAtUtc = _timeProvider.GetUtcNow() };
        Replace(item);
        await _store.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
    }

    private void OnTransferChanged(object? sender, OutgoingFileTransferChangedEventArgs args)
    {
        var item = _active;
        if (item is null || item.TransferId != args.Transfer.TransferId) return;
        item = item with
        {
            Sha256 = args.Transfer.Sha256,
            CanResume = item.CanResume || args.Transfer.State is OutgoingFileTransferState.Sending
                or OutgoingFileTransferState.WaitingForConfirmation or OutgoingFileTransferState.Completed,
            AcknowledgedOffset = args.Transfer.SentBytes,
            NextChunkIndex = args.Transfer.NextChunkIndex,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        _active = item;
        Replace(item);
        _ = _store.UpsertAsync(item, _lifetime.Token);
    }

    private bool TryGetNext(out OutgoingTransferQueueItem item)
    {
        lock (_gate)
        {
            item = _items.OrderBy(candidate => candidate.CreatedAtUtc)
                .FirstOrDefault(candidate => candidate.State == OutgoingTransferQueueState.Queued)!;
            return item is not null;
        }
    }

    private void Replace(OutgoingTransferQueueItem item)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(candidate => candidate.TransferId == item.TransferId);
            if (index >= 0) _items[index] = item; else _items.Add(item);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _manager.Changed -= OnTransferChanged;
        _lifetime.Cancel();
        if (_worker is not null) try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}
