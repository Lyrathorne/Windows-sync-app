# Windows Phone Files

P14 adds the Windows consumer for `media-catalog-v1` and `thumbnails-v1`.

## Architecture

- `MediaCatalogManager` belongs to `DeviceSync.Application`. It works only with `IFeatureMessageTransport`; the WPF view model never reads or writes a socket.
- `ClientSession` remains the single frame reader and routes catalog responses through `FeatureMessageTransport`.
- Capability negotiation enables the feature only when Android and Windows both advertise `media-catalog-v1`. Thumbnails additionally require `thumbnails-v1`.
- One catalog page is outstanding at a time. Closing the view, replacing a query, cancellation, or disconnect sends/cancels the corresponding request.

## Files window

The **Phone files** action opens a dedicated Files window with:

- Photos, Videos, Documents, and Recent categories;
- name search and supported sorting;
- grid and virtualized list presentations;
- pages of 100 items with explicit “Load more” backpressure;
- a 1,000-item in-memory presentation window, preventing an accidental 10–100k item UI allocation;
- placeholders, loading, offline, permission, empty, and error states;
- thumbnails requested only when the item template is loaded;
- multi-selection and sequential downloads, respecting the V1 one-active-transfer invariant.

## Safe downloads

Windows generates the `transferId` before sending `catalog.file.download.request`. `CatalogDownloadAuthorizer` accepts only a subsequent `file.offer` with that exact ID and matching name, MIME type, and reported size. Other file offers retain the existing user/policy decision flow.

Downloads:

- use a user-selected destination folder;
- run sequentially;
- show verified incoming-transfer progress;
- support cancellation from Windows;
- are exposed as locally openable only after the incoming manager completes size and SHA-256 verification and atomically commits the file.

## Thumbnail cache

`WindowsMediaThumbnailCache` stores only bounded encoded previews:

- maximum item size: 256 KiB;
- default disk budget: 64 MiB;
- SHA-256 cache filenames, containing no user filenames or Android IDs;
- least-recently-accessed eviction;
- corrupt or oversized entries are deleted;
- original media is never cached by the gallery.

## Invalidations

- A different snapshot generation on a later page is rejected as stale.
- `catalog.changed` with `requiresRefresh=true` refreshes the current query.
- permission denial/revoke clears the visible catalog.
- disconnect cancels pending pages and thumbnails and removes outstanding download authorization.

Tests cover paging, opaque tokens, stale pages, cancellation, reconnect, download authorization, capability negotiation, and cache eviction.
