# Clipboard and Text Sharing V1

Capabilities: `clipboard-v1` and `text-share-v1`.

- `clipboard.update` supports UTF-8 text and HTTP(S) links up to 256 KiB.
- Clipboard synchronization is disabled by default and persisted as an opt-in setting.
- Every update has a globally unique `revisionId` and `sourceDeviceId`.
- A revision is applied once; applying a remote value must not generate a new update.
- `text.share` is an explicit user action and stores at most 50 recent items per process.
- A received URL is never opened automatically. Only `http` and `https` are accepted
  for the user-initiated Open action; other values are treated as plain text.
- Android exposes a `text/plain` share target.

Neither pairing secrets nor clipboard/text contents may be logged.
