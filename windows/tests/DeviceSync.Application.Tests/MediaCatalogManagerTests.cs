using System.Text.Json;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class MediaCatalogManagerTests
{
    [Fact]
    public async Task Pagination_UsesOpaqueNextTokenAndStableSnapshot()
    {
        var transport = new FakeTransport();
        var manager = new MediaCatalogManager(transport, new());
        var firstTask = manager.StartQueryAsync(new(["image"], PageSize: 2));
        var firstQuery = transport.DecodeLast<CatalogQueryPayload>(ProtocolMessageTypes.CatalogQuery);
        transport.Route(ProtocolMessageTypes.CatalogPage, new CatalogPagePayload
        {
            QueryId = firstQuery.QueryId,
            Items = [Item("one")],
            NextPageToken = "opaque-2",
            SnapshotGeneration = 7,
            HasMore = true,
        });
        var first = await firstTask;
        Assert.True(first.HasMore);

        var nextTask = manager.LoadNextPageAsync();
        var nextQuery = transport.DecodeLast<CatalogQueryPayload>(ProtocolMessageTypes.CatalogQuery);
        Assert.Equal("opaque-2", nextQuery.PageToken);
        transport.Route(ProtocolMessageTypes.CatalogPage, new CatalogPagePayload
        {
            QueryId = firstQuery.QueryId,
            Items = [Item("two")],
            SnapshotGeneration = 7,
            HasMore = false,
        });
        Assert.False((await nextTask)!.HasMore);
    }

    [Fact]
    public async Task StaleSecondPage_IsRejected()
    {
        var transport = new FakeTransport();
        var manager = new MediaCatalogManager(transport, new());
        var firstTask = manager.StartQueryAsync(new(["image"]));
        var query = transport.DecodeLast<CatalogQueryPayload>(ProtocolMessageTypes.CatalogQuery);
        transport.Route(ProtocolMessageTypes.CatalogPage, new CatalogPagePayload
        {
            QueryId = query.QueryId, Items = [], NextPageToken = "next", SnapshotGeneration = 3, HasMore = true,
        });
        await firstTask;

        var nextTask = manager.LoadNextPageAsync();
        transport.Route(ProtocolMessageTypes.CatalogPage, new CatalogPagePayload
        {
            QueryId = query.QueryId, Items = [], SnapshotGeneration = 4, HasMore = false,
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await nextTask!);
    }

    [Fact]
    public async Task Cancellation_SendsCatalogCancelAndStopsPendingPage()
    {
        var transport = new FakeTransport();
        var manager = new MediaCatalogManager(transport, new());
        using var cancellation = new CancellationTokenSource();
        var task = manager.StartQueryAsync(new(["image"]), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Contains(transport.Sent, message => message.Type == ProtocolMessageTypes.CatalogCancel);
    }

    [Fact]
    public async Task Disconnect_CancelsQueryAndClearsAvailability()
    {
        var transport = new FakeTransport();
        var manager = new MediaCatalogManager(transport, new());
        var task = manager.StartQueryAsync(new(["image"]));
        transport.Disconnect();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.False(manager.IsAvailable);
    }

    [Fact]
    public async Task Factory_CreatesIndependentCatalogSessions()
    {
        var transport = new FakeTransport();
        var factory = new MediaCatalogClientFactory(
            transport,
            new CatalogDownloadAuthorizer(),
            new FakeThumbnailCache());
        using var recent = factory.Create();
        using var fullWindow = factory.Create();

        var recentTask = recent.StartQueryAsync(new(["image"], PageSize: 6));
        var fullTask = fullWindow.StartQueryAsync(new(["document"], PageSize: 100));
        var queries = transport.Sent
            .Where(message => message.Type == ProtocolMessageTypes.CatalogQuery)
            .Select(message => ProtocolSerializer.DecodePayload<CatalogQueryPayload>(message.Payload))
            .ToArray();

        Assert.Equal(2, queries.Length);
        Assert.NotEqual(queries[0].QueryId, queries[1].QueryId);
        transport.Route(ProtocolMessageTypes.CatalogPage, new CatalogPagePayload
        {
            QueryId = queries[0].QueryId,
            Items = [Item("recent")],
            SnapshotGeneration = 8,
            HasMore = false,
        });
        transport.Route(ProtocolMessageTypes.CatalogPage, new CatalogPagePayload
        {
            QueryId = queries[1].QueryId,
            Items = [Item("full")],
            SnapshotGeneration = 8,
            HasMore = false,
        });

        Assert.Equal("recent", (await recentTask).Items.Single().ItemId);
        Assert.Equal("full", (await fullTask).Items.Single().ItemId);
        Assert.DoesNotContain(transport.Sent, message =>
            message.Type == ProtocolMessageTypes.CatalogCancel);
    }

    [Fact]
    public async Task PermissionResponse_IsDeliveredOnlyToOwningCatalogSession()
    {
        var transport = new FakeTransport();
        var factory = new MediaCatalogClientFactory(transport, new(), new FakeThumbnailCache());
        using var recent = factory.Create();
        using var fullWindow = factory.Create();
        var recentPermissions = 0;
        var fullPermissions = 0;
        recent.PermissionChanged += _ => recentPermissions++;
        fullWindow.PermissionChanged += _ => fullPermissions++;

        var recentTask = recent.StartQueryAsync(new(["image"]));
        var fullTask = fullWindow.StartQueryAsync(new(["document"]));
        var queries = transport.Sent.Where(message => message.Type == ProtocolMessageTypes.CatalogQuery)
            .Select(message => ProtocolSerializer.DecodePayload<CatalogQueryPayload>(message.Payload)).ToArray();

        transport.Route(ProtocolMessageTypes.CatalogPermission, new CatalogPermissionPayload
        {
            RequestId = queries[1].QueryId,
            State = "denied",
            GrantedCategories = [],
            CanRequest = true,
            ReasonCode = "permission_required",
        });

        Assert.Equal(0, recentPermissions);
        Assert.Equal(1, fullPermissions);
        foreach (var query in queries)
            transport.Route(ProtocolMessageTypes.CatalogPage, new CatalogPagePayload
            {
                QueryId = query.QueryId, Items = [], SnapshotGeneration = 1, HasMore = false,
            });
        await Task.WhenAll(recentTask, fullTask);
    }

    [Fact]
    public void DownloadAuthorizer_AcceptsOnlyMatchingRequestedOffer()
    {
        var authorizer = new CatalogDownloadAuthorizer();
        var item = Item("safe");
        authorizer.Register("transfer-1", item, Path.GetTempPath());
        var accepted = authorizer.Authorize(new FileOfferPayload
        {
            TransferId = "transfer-1",
            FileName = item.DisplayName,
            MimeType = item.MimeType,
            SizeBytes = item.SizeBytes!.Value,
            Sha256 = Convert.ToBase64String(new byte[32]),
            ChunkSize = 65_536,
        });
        Assert.True(accepted!.Accepted);
        Assert.Null(authorizer.Authorize(new FileOfferPayload
        {
            TransferId = "unknown", FileName = "x", MimeType = "text/plain", SizeBytes = 1,
            Sha256 = Convert.ToBase64String(new byte[32]), ChunkSize = 65_536,
        }));
    }

    [Fact]
    public async Task DiskCache_EvictsOldestEntriesAtBound()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"devicesync-thumbnail-test-{Guid.NewGuid()}");
        try
        {
            var cache = new WindowsMediaThumbnailCache(directory, maximumBytes: 700);
            await cache.PutAsync("one", new("image/jpeg", 10, 10, new byte[300]));
            await Task.Delay(20);
            await cache.PutAsync("two", new("image/jpeg", 10, 10, new byte[300]));
            Assert.Null(await cache.GetAsync("one"));
            Assert.NotNull(await cache.GetAsync("two"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static CatalogItemPayload Item(string id) => new()
    {
        ItemId = id,
        DisplayName = $"{id}.jpg",
        MimeType = "image/jpeg",
        SizeBytes = 100,
        ModifiedAtUtc = "2026-07-16T00:00:00Z",
        Category = "image",
        Generation = 7,
        Revision = "rev-1",
        ThumbnailAvailable = true,
    };

    private sealed class FakeTransport : IFeatureMessageTransport
    {
        public bool IsConnected { get; private set; } = true;
        public string? LocalDeviceId => "windows";
        public string? RemoteDeviceId => "android";
        public IReadOnlyCollection<string> Capabilities { get; private set; } =
            [SupportedCapabilities.MediaCatalogV1, SupportedCapabilities.ThumbnailsV1];
        public List<(string Type, JsonElement Payload)> Sent { get; } = [];
        public event EventHandler<FeatureMessageEventArgs>? MessageReceived;
        public event EventHandler? ConnectionChanged;

        public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload));
            return Task.CompletedTask;
        }

        public T DecodeLast<T>(string type) =>
            ProtocolSerializer.DecodePayload<T>(Sent.Last(message => message.Type == type).Payload);

        public void Route<T>(string type, T payload) =>
            MessageReceived?.Invoke(this, new(type, ProtocolSerializer.PayloadToJson(payload)));

        public void Disconnect()
        {
            IsConnected = false;
            Capabilities = [];
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeThumbnailCache : IMediaThumbnailCache
    {
        public Task<CachedMediaThumbnail?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<CachedMediaThumbnail?>(null);

        public Task PutAsync(string key, CachedMediaThumbnail thumbnail, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
