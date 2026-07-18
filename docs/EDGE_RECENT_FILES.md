# Edge panel recent phone files

P15 adds a compact recent-files block to the Windows edge panel.

## Behaviour

- The block is active only while the edge panel is expanded and the **Files** section is selected.
- It requests at most six items, ordered by `modifiedAtUtc` descending.
- Each row shows the file type, localised modification time, size, and an optional 96×72 thumbnail.
- Selecting a row opens the full Phone files window. Original files are never downloaded automatically.
- Loading uses skeleton rows; disconnected or unsupported phones show an offline status.
- Refresh is explicit and catalog-change notifications can also trigger a refresh.
- Collapsing the panel or leaving the Files section cancels the compact query, so the panel does not retain catalog work or block edge animation.

## Isolation and cache

The compact panel and the full Phone files window use separate `MediaCatalogManager` instances created by `MediaCatalogClientFactory`. Their queries therefore have different IDs and cannot cancel one another.

Both clients share `WindowsMediaThumbnailCache`. It is disk-backed, bounded to 64 MiB by default, keyed by item revision and requested dimensions, and evicts the oldest files when the limit is exceeded.

## Privacy

Recent thumbnails are hidden by default. The user can opt in from the Files section of the edge panel.

Thumbnails are also cleared and hidden while Windows reports:

- a locked session;
- a Remote Desktop / terminal-server session.

Windows does not expose a reliable universal API for detecting every third-party screen-sharing application. The manual privacy switch therefore remains the guaranteed protection for those sessions. File names and metadata remain visible in the compact list; users who need stronger concealment should collapse or disable the edge panel.

## Accessibility

Rows are regular focusable buttons with automation names. Keyboard navigation works through the tab order, while `Alt+3` selects the Files section and `Escape` collapses the panel.
