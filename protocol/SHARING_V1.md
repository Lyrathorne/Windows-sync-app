# Clipboard and Text Sharing V1

Capabilities: `clipboard-v1` and `text-share-v1`.

- `clipboard.update` supports UTF-8 text and HTTP(S) links up to 256 KiB.
- Clipboard synchronization is disabled by default and persisted as an opt-in setting.
- Every update has a globally unique `revisionId` and `originDeviceId`.
- A revision is applied once; applying a remote value must not generate a new update.
- `text.share` is an explicit user action and stores at most 50 recent items per process.
- A received URL is never opened automatically. Only `http` and `https` are accepted
  for the user-initiated Open action; other values are treated as plain text.
- Android exposes a `text/plain` share target.

Neither pairing secrets nor clipboard/text contents may be logged.

Automatic clipboard also requires global opt-in and explicit per-device
authorization. Existing trust records migrate with automatic clipboard
disabled. See `../docs/TRUST_AND_AUTOMATION_POLICY.md`.

## Delivery and privacy rules

- Local changes are debounced for 300–400 ms and coalesced to the newest value.
- Identical text and already-seen revisions are ignored. Receivers keep a bounded,
  expiring revision cache instead of an unbounded session set.
- During a short disconnect, only the newest pending value is held in volatile
  memory. It expires after 2 minutes and is never persisted to disk.
- Empty/blank values never overwrite the other device's clipboard.
- Password/private/incognito editor contexts and Windows clipboard entries marked
  `ExcludeClipboardContentFromMonitorProcessing` or excluded from clipboard history
  are not automatically sent.
- `originDeviceId` must match the authenticated message sender. A mismatch is a
  protocol/security error, not a value that may be forwarded.
- Logs may contain revision IDs, byte counts and technical result codes, but never
  clipboard text.

## Android platform limitation

An ordinary Android application cannot continuously read the system clipboard while
it is in the background. DeviceSync therefore observes automatic changes while its
activity is visible and while the DeviceSync IME is active. A foreground service does
not bypass Android's clipboard privacy restrictions. Manual sharing remains available
through the Android share sheet and the DeviceSync keyboard clipboard panel.
