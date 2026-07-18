using System.Collections.Concurrent;
using DeviceSync.Protocol;

namespace DeviceSync.Application;

public sealed record MediaCatalogQuery(
    IReadOnlyList<string> Categories,
    string? Search = null,
    string SortBy = "modifiedAtUtc",
    string SortDirection = "desc",
    string? AlbumId = null,
    int PageSize = 100);

public sealed record MediaCatalogPage(
    IReadOnlyList<CatalogItemPayload> Items,
    bool HasMore,
    long SnapshotGeneration);

public sealed record MediaCatalogPermission(
    string State,
    IReadOnlyList<string> GrantedCategories,
    bool CanRequest,
    string? ReasonCode);

public sealed class MediaCatalogChangedEventArgs(long generation, bool requiresRefresh) : EventArgs
{
    public long Generation { get; } = generation;
    public bool RequiresRefresh { get; } = requiresRefresh;
}

public sealed class MediaCatalogManager : IDisposable
{
    private readonly IFeatureMessageTransport _transport;
    private readonly CatalogDownloadAuthorizer _downloads;
    private readonly IMediaThumbnailCache? _thumbnailCache;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CatalogPagePayload>> _pages = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CatalogThumbnailResponsePayload>> _thumbnails = new();
    private readonly ConcurrentDictionary<string, string> _downloadRequests = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string? _queryId;
    private string? _nextPageToken;
    private MediaCatalogQuery? _activeQuery;
    private long _snapshotGeneration;
    private CancellationTokenSource _lifetime = new();

    public MediaCatalogManager(
        IFeatureMessageTransport transport,
        CatalogDownloadAuthorizer downloads,
        IMediaThumbnailCache? thumbnailCache = null)
    {
        _transport = transport;
        _downloads = downloads;
        _thumbnailCache = thumbnailCache;
        _transport.MessageReceived += OnMessageReceived;
        _transport.ConnectionChanged += OnConnectionChanged;
    }

    public bool IsAvailable => _transport.IsConnected &&
        _transport.Capabilities.Contains(SupportedCapabilities.MediaCatalogV1, StringComparer.Ordinal);
    public bool ThumbnailsAvailable => IsAvailable &&
        _transport.Capabilities.Contains(SupportedCapabilities.ThumbnailsV1, StringComparer.Ordinal);
    public MediaCatalogPermission? Permission { get; private set; }
    public event EventHandler? ConnectionChanged;
    public event EventHandler<MediaCatalogChangedEventArgs>? CatalogChanged;
    public event Action<MediaCatalogPermission>? PermissionChanged;
    public event Action<string>? Error;
    public event Action<string, MediaCatalogException>? DownloadFailed;

    public async Task<MediaCatalogPage> StartQueryAsync(MediaCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ValidateAvailability();
        await CancelActiveQueryAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            _queryId = Guid.NewGuid().ToString();
            _nextPageToken = null;
            _activeQuery = query;
            _snapshotGeneration = 0;
        }
        return await RequestPageAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaCatalogPage?> LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_activeQuery is null || _queryId is null || _nextPageToken is null) return null;
        }
        return await RequestPageAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CatalogThumbnailResponsePayload?> GetThumbnailAsync(
        CatalogItemPayload item,
        int maxWidth = 256,
        int maxHeight = 256,
        CancellationToken cancellationToken = default)
    {
        if (!ThumbnailsAvailable || !item.ThumbnailAvailable) return null;
        var cacheKey = $"{item.ItemId}|{item.Revision}|{Math.Clamp(maxWidth, 32, 512)}|{Math.Clamp(maxHeight, 32, 512)}";
        if (_thumbnailCache is not null)
        {
            var cached = await _thumbnailCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return new CatalogThumbnailResponsePayload
                {
                    RequestId = "cache",
                    ItemId = item.ItemId,
                    Revision = item.Revision,
                    MimeType = cached.MimeType,
                    Width = cached.Width,
                    Height = cached.Height,
                    SizeBytes = cached.Data.LongLength,
                    Data = Convert.ToBase64String(cached.Data),
                };
            }
        }
        var requestId = Guid.NewGuid().ToString();
        var completion = NewCompletion<CatalogThumbnailResponsePayload>();
        if (!_thumbnails.TryAdd(requestId, completion)) throw new InvalidOperationException("Duplicate request ID.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
        try
        {
            await _transport.SendAsync(ProtocolMessageTypes.CatalogThumbnailRequest,
                ProtocolSerializer.PayloadToJson(new CatalogThumbnailRequestPayload
                {
                    RequestId = requestId,
                    ItemId = item.ItemId,
                    ExpectedRevision = item.Revision,
                    MaxWidth = Math.Clamp(maxWidth, 32, 512),
                    MaxHeight = Math.Clamp(maxHeight, 32, 512),
                    Format = "jpeg",
                    Quality = 80,
                }), linked.Token).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), linked.Token).ConfigureAwait(false);
            if (_thumbnailCache is not null)
            {
                var bytes = Convert.FromBase64String(result.Data);
                if (bytes.LongLength == result.SizeBytes && bytes.Length <= 256 * 1024)
                    await _thumbnailCache.PutAsync(cacheKey,
                        new(result.MimeType, result.Width, result.Height, bytes), linked.Token).ConfigureAwait(false);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            await SendCancelQuietlyAsync(requestId, "thumbnail_cancelled").ConfigureAwait(false);
            throw;
        }
        finally { _thumbnails.TryRemove(requestId, out _); }
    }

    public async Task<string> RequestDownloadAsync(
        CatalogItemPayload item,
        string destinationDirectory,
        string? requestedTransferId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAvailability();
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("Destination is required.", nameof(destinationDirectory));
        var requestId = Guid.NewGuid().ToString();
        var transferId = requestedTransferId ?? Guid.NewGuid().ToString();
        _downloads.Register(transferId, item, destinationDirectory);
        _downloadRequests[transferId] = requestId;
        try
        {
            await _transport.SendAsync(ProtocolMessageTypes.CatalogFileDownloadRequest,
                ProtocolSerializer.PayloadToJson(new CatalogFileDownloadRequestPayload
                {
                    RequestId = requestId,
                    ItemId = item.ItemId,
                    ExpectedRevision = item.Revision,
                    TransferId = transferId,
                }), cancellationToken).ConfigureAwait(false);
            return transferId;
        }
        catch
        {
            _downloads.Cancel(transferId);
            _downloadRequests.TryRemove(transferId, out _);
            throw;
        }
    }

    public async Task CancelDownloadAsync(string transferId, CancellationToken cancellationToken = default)
    {
        _downloads.Cancel(transferId);
        if (_downloadRequests.TryRemove(transferId, out var requestId))
            await SendCancelQuietlyAsync(requestId, "user_cancelled").ConfigureAwait(false);
    }

    public void CompleteDownload(string transferId)
    {
        _downloads.Cancel(transferId);
        _downloadRequests.TryRemove(transferId, out _);
    }

    public async Task CancelActiveQueryAsync(CancellationToken cancellationToken = default)
    {
        string? queryId;
        lock (_gate)
        {
            queryId = _queryId;
            _queryId = null;
            _nextPageToken = null;
            _activeQuery = null;
        }
        if (queryId is null) return;
        if (_pages.TryRemove(queryId, out var pending)) pending.TrySetCanceled(cancellationToken);
        await SendCancelQuietlyAsync(queryId, "view_closed").ConfigureAwait(false);
    }

    private async Task<MediaCatalogPage> RequestPageAsync(CancellationToken cancellationToken)
    {
        string queryId;
        string? pageToken;
        MediaCatalogQuery query;
        lock (_gate)
        {
            queryId = _queryId ?? throw new InvalidOperationException("No active query.");
            pageToken = _nextPageToken;
            query = _activeQuery ?? throw new InvalidOperationException("No active query.");
        }
        var completion = NewCompletion<CatalogPagePayload>();
        if (!_pages.TryAdd(queryId, completion)) throw new InvalidOperationException("A catalog page is already pending.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
        try
        {
            await _transport.SendAsync(ProtocolMessageTypes.CatalogQuery,
                ProtocolSerializer.PayloadToJson(new CatalogQueryPayload
                {
                    QueryId = queryId,
                    PageSize = Math.Clamp(query.PageSize, 1, 200),
                    PageToken = pageToken,
                    Categories = query.Categories,
                    Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
                    SortBy = query.SortBy,
                    SortDirection = query.SortDirection,
                    AlbumId = query.AlbumId,
                }), linked.Token).ConfigureAwait(false);
            var page = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_queryId != queryId) throw new OperationCanceledException("The catalog query was replaced.");
                if (_snapshotGeneration != 0 && page.SnapshotGeneration != _snapshotGeneration)
                    throw new InvalidDataException("The catalog page belongs to a stale snapshot.");
                _snapshotGeneration = page.SnapshotGeneration;
                _nextPageToken = page.HasMore ? page.NextPageToken : null;
            }
            return new MediaCatalogPage(page.Items, page.HasMore, page.SnapshotGeneration);
        }
        catch (OperationCanceledException)
        {
            await SendCancelQuietlyAsync(queryId, "query_cancelled").ConfigureAwait(false);
            throw;
        }
        finally { _pages.TryRemove(queryId, out _); }
    }

    private async void OnMessageReceived(object? sender, FeatureMessageEventArgs args)
    {
        try
        {
            switch (args.Type)
            {
                case ProtocolMessageTypes.CatalogPage:
                    var page = ProtocolSerializer.DecodePayload<CatalogPagePayload>(args.Payload);
                    if (_pages.TryGetValue(page.QueryId, out var pageCompletion)) pageCompletion.TrySetResult(page);
                    break;
                case ProtocolMessageTypes.CatalogThumbnailResponse:
                    var thumbnail = ProtocolSerializer.DecodePayload<CatalogThumbnailResponsePayload>(args.Payload);
                    if (_thumbnails.TryGetValue(thumbnail.RequestId, out var thumbnailCompletion))
                        thumbnailCompletion.TrySetResult(thumbnail);
                    break;
                case ProtocolMessageTypes.CatalogChanged:
                    var changed = ProtocolSerializer.DecodePayload<CatalogChangedPayload>(args.Payload);
                    CatalogChanged?.Invoke(this, new(changed.Generation, changed.RequiresRefresh));
                    break;
                case ProtocolMessageTypes.CatalogPermission:
                    var permission = ProtocolSerializer.DecodePayload<CatalogPermissionPayload>(args.Payload);
                    if (permission.RequestId is { Length: > 0 } requestId && !OwnsRequest(requestId))
                        break;
                    Permission = new(permission.State, permission.GrantedCategories, permission.CanRequest, permission.ReasonCode);
                    PermissionChanged?.Invoke(Permission);
                    break;
                case ProtocolMessageTypes.CatalogError:
                    HandleError(ProtocolSerializer.DecodePayload<CatalogErrorPayload>(args.Payload));
                    break;
            }
        }
        catch (Exception exception) { Error?.Invoke(exception.Message); }
        await Task.CompletedTask;
    }

    private void HandleError(CatalogErrorPayload error)
    {
        var exception = new MediaCatalogException(error.Code, error.Message ?? error.Code, error.Retryable);
        if (error.RequestId is { } requestId)
        {
            if (_pages.TryGetValue(requestId, out var page)) page.TrySetException(exception);
            if (_thumbnails.TryGetValue(requestId, out var thumbnail)) thumbnail.TrySetException(exception);
            var download = _downloadRequests.FirstOrDefault(entry => entry.Value == requestId);
            if (!string.IsNullOrEmpty(download.Key))
            {
                _downloadRequests.TryRemove(download.Key, out _);
                _downloads.Cancel(download.Key);
                DownloadFailed?.Invoke(download.Key, exception);
            }
        }
        Error?.Invoke(exception.Message);
    }

    private bool OwnsRequest(string requestId)
    {
        if (_pages.ContainsKey(requestId) || _thumbnails.ContainsKey(requestId) ||
            _downloadRequests.Values.Contains(requestId, StringComparer.Ordinal)) return true;
        lock (_gate) return string.Equals(_queryId, requestId, StringComparison.Ordinal);
    }

    private void OnConnectionChanged(object? sender, EventArgs args)
    {
        var old = Interlocked.Exchange(ref _lifetime, new());
        old.Cancel();
        old.Dispose();
        if (!_transport.IsConnected)
        {
            lock (_gate) { _queryId = null; _nextPageToken = null; _activeQuery = null; }
            Permission = null;
            _downloads.CancelAll();
            _downloadRequests.Clear();
        }
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendCancelQuietlyAsync(string requestId, string reason)
    {
        if (!_transport.IsConnected) return;
        try
        {
            await _transport.SendAsync(ProtocolMessageTypes.CatalogCancel,
                ProtocolSerializer.PayloadToJson(new CatalogCancelPayload { RequestId = requestId, Reason = reason }))
                .ConfigureAwait(false);
        }
        catch { }
    }

    private void ValidateAvailability()
    {
        if (!IsAvailable) throw new InvalidOperationException("The connected phone does not provide its file catalog.");
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _transport.MessageReceived -= OnMessageReceived;
        _transport.ConnectionChanged -= OnConnectionChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed record CachedMediaThumbnail(string MimeType, int Width, int Height, byte[] Data);

public interface IMediaThumbnailCache
{
    Task<CachedMediaThumbnail?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task PutAsync(string key, CachedMediaThumbnail thumbnail, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class MediaCatalogException(string code, string message, bool retryable) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed class MediaCatalogClientFactory(
    IFeatureMessageTransport transport,
    CatalogDownloadAuthorizer downloads,
    IMediaThumbnailCache thumbnailCache)
{
    public MediaCatalogManager Create() => new(transport, downloads, thumbnailCache);
}

public sealed class CatalogDownloadAuthorizer : IFolderFileTransferAuthorizer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ExpectedDownload> _expected = new(StringComparer.Ordinal);

    public void Register(string transferId, CatalogItemPayload item, string destinationDirectory)
    {
        lock (_gate)
            _expected[transferId] = new(item.DisplayName, item.MimeType, item.SizeBytes, Path.GetFullPath(destinationDirectory));
    }

    public void Cancel(string transferId) { lock (_gate) _expected.Remove(transferId); }
    public void CancelAll() { lock (_gate) _expected.Clear(); }

    public IncomingFileTransferDecision? Authorize(FileOfferPayload offer)
    {
        ExpectedDownload? expected;
        lock (_gate)
        {
            if (!_expected.Remove(offer.TransferId, out expected)) return null;
        }
        if (!string.Equals(expected.DisplayName, offer.FileName, StringComparison.Ordinal) ||
            !string.Equals(expected.MimeType, offer.MimeType, StringComparison.OrdinalIgnoreCase) ||
            expected.SizeBytes is { } size && size != offer.SizeBytes)
            return new(false, RejectionCode: "catalog_item_changed");
        return new(true, expected.DestinationDirectory, DestinationFileName: expected.DisplayName);
    }

    private sealed record ExpectedDownload(string DisplayName, string MimeType, long? SizeBytes, string DestinationDirectory);
}

public sealed class CompositeFileTransferAuthorizer(params IFolderFileTransferAuthorizer[] authorizers) : IFolderFileTransferAuthorizer
{
    public IncomingFileTransferDecision? Authorize(FileOfferPayload offer)
    {
        foreach (var authorizer in authorizers)
        {
            var result = authorizer.Authorize(offer);
            if (result is not null) return result;
        }
        return null;
    }
}
