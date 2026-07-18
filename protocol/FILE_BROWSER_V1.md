# DeviceSync File Browser V1

Capability: `file-browser-v1`.

All paths are opaque provider IDs. Windows must never construct Android filesystem paths.
Android exposes only roots explicitly granted through the Storage Access Framework.

## Messages

| Type | Direction | Purpose |
| --- | --- | --- |
| `files.roots.query` | Windows → Android | Enumerate granted roots |
| `files.roots.page` | Android → Windows | Return root descriptors |
| `files.children.query` | Windows → Android | Page through one folder |
| `files.children.page` | Android → Windows | Return folders/files and breadcrumbs |
| `files.download.request` | Windows → Android | Start verified resumable download |
| `files.upload.offer` | Windows → Android | Offer upload into selected folder |
| `files.changed` | Android → Windows | Invalidate one folder generation |
| `files.cancel` | Either | Cancel a query or transfer |
| `files.error` | Either | Stable typed failure |

Every query contains `requestId`, `parentId`, `pageSize` (maximum 200), optional
`pageToken`, `sortBy`, `sortDirection` and optional search text. A page contains an opaque
`nextPageToken`, `generation`, breadcrumbs and items.

An item contains `itemId`, `parentId`, `displayName`, `mimeType`, `isDirectory`, optional
`sizeBytes`, `modifiedAtUtc`, `revision`, `canRead`, `canWrite` and `thumbnailAvailable`.

Downloads and uploads reuse DeviceSync file-transfer chunks, SHA-256 verification,
acknowledged offsets, cancellation and resume tokens. Android must compare `revision`
before opening a download. Upload completion is atomic: write a temporary document, verify
size/hash, then rename. Never overwrite without an explicit conflict policy.

Stable errors: `PERMISSION_REQUIRED`, `PERMISSION_REVOKED`, `ROOT_NOT_FOUND`,
`ITEM_NOT_FOUND`, `ITEM_CHANGED`, `INVALID_PARENT`, `READ_ONLY`, `CONFLICT`,
`INSUFFICIENT_SPACE`, `CANCELLED`, `UNSUPPORTED`.

