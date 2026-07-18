namespace DeviceSync.Protocol;

public sealed record CatalogQueryPayload
{
    public required string QueryId { get; init; }
    public int PageSize { get; init; } = 100;
    public string? PageToken { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> MimeTypes { get; init; } = [];
    public string? AlbumId { get; init; }
    public string? Search { get; init; }
    public string SortBy { get; init; } = "modifiedAtUtc";
    public string SortDirection { get; init; } = "desc";
    public string? ModifiedAfterUtc { get; init; }
    public long? GenerationAfter { get; init; }
}

public sealed record CatalogItemPayload
{
    public required string ItemId { get; init; }
    public required string DisplayName { get; init; }
    public required string MimeType { get; init; }
    public long? SizeBytes { get; init; }
    public string? CreatedAtUtc { get; init; }
    public required string ModifiedAtUtc { get; init; }
    public required string Category { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? DurationMillis { get; init; }
    public string? AlbumId { get; init; }
    public string? AlbumDisplayName { get; init; }
    public required long Generation { get; init; }
    public required string Revision { get; init; }
    public bool ThumbnailAvailable { get; init; }
}

public sealed record CatalogPagePayload
{
    public required string QueryId { get; init; }
    public required IReadOnlyList<CatalogItemPayload> Items { get; init; }
    public string? NextPageToken { get; init; }
    public required long SnapshotGeneration { get; init; }
    public required bool HasMore { get; init; }
}

public sealed record CatalogChangePayload
{
    public required string ItemId { get; init; }
    public required string ChangeType { get; init; }
    public string? Revision { get; init; }
}

public sealed record CatalogChangedPayload
{
    public required long Generation { get; init; }
    public IReadOnlyList<CatalogChangePayload> Changes { get; init; } = [];
    public bool RequiresRefresh { get; init; }
}

public sealed record CatalogThumbnailRequestPayload
{
    public required string RequestId { get; init; }
    public required string ItemId { get; init; }
    public string? ExpectedRevision { get; init; }
    public int MaxWidth { get; init; } = 512;
    public int MaxHeight { get; init; } = 512;
    public string Format { get; init; } = "jpeg";
    public int Quality { get; init; } = 80;
}

public sealed record CatalogThumbnailResponsePayload
{
    public required string RequestId { get; init; }
    public required string ItemId { get; init; }
    public required string Revision { get; init; }
    public required string MimeType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long SizeBytes { get; init; }
    public required string Data { get; init; }
}

public sealed record CatalogFileDownloadRequestPayload
{
    public required string RequestId { get; init; }
    public required string ItemId { get; init; }
    public string? ExpectedRevision { get; init; }
    public required string TransferId { get; init; }
}

public sealed record CatalogPermissionPayload
{
    public string? RequestId { get; init; }
    public required string State { get; init; }
    public IReadOnlyList<string> GrantedCategories { get; init; } = [];
    public bool CanRequest { get; init; }
    public string? ReasonCode { get; init; }
}

public sealed record CatalogErrorPayload
{
    public string? RequestId { get; init; }
    public required string Code { get; init; }
    public string? Message { get; init; }
    public bool Retryable { get; init; }
    public long? CurrentGeneration { get; init; }
}

public sealed record CatalogCancelPayload
{
    public required string RequestId { get; init; }
    public required string Reason { get; init; }
}
