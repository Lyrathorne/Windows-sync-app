# DeviceSync Android reference patch

The Android application source is not present in this repository. This document is the
implementation contract for the Android repository; it deliberately does not claim to
produce an APK from Windows-only sources.

## Background connection service

Add one process-wide `DeviceSyncConnectionService` started with
`ContextCompat.startForegroundService`. The service owns the only connection coordinator
and socket. Activities bind to it; they must never create a second socket.

Manifest requirements:

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
<uses-permission android:name="android.permission.RECEIVE_BOOT_COMPLETED" />
```

Declare the service with `foregroundServiceType="connectedDevice|dataSync"`. Start it only
after an explicit user opt-in. `START_STICKY` may restore a previously enabled service;
manual disconnect must persist `backgroundEnabled=false`, close the coordinator, cancel
scheduled reconnects and call `stopSelf()`.

The permanent notification channel must be low importance and expose Disconnect. Publish
service state (`starting`, `discovering`, `connecting`, `connected`, `backoff`, `stopped`),
the selected endpoint and a redacted last-disconnect reason through a `StateFlow`.

Use `ConnectivityManager.registerDefaultNetworkCallback` only as a wake-up signal. Build
candidate LAN endpoints from QR, mDNS, `DeviceSyncLanBeaconV1`, remembered endpoints and
manual IP. Deduplicate them, then race at most three endpoints. A winner is accepted only
after TLS SPKI pinning and the DeviceSync identity handshake. Cancel and close every loser.

Reconnect delays are `1, 2, 4, 8, 15, 30, 60` seconds with ±20% jitter and a 60-second cap.
Reset the attempt counter after 30 seconds of a healthy authenticated session. Do not retry
fatal identity errors. `TRUST_REVOKED` stops reconnect and requires QR; `PAIRING_REQUIRED`
switches an explicitly scanned QR flow to `pairing.request`.

Do not keep a permanent wakelock. A partial wakelock may cover one connection attempt or an
active transfer and must have a hard timeout. Use WorkManager only for deferred recovery;
it is not the owner of the live socket. A boot receiver may restart the service only when
the persisted user preference allows it.

## Battery UX

Show current battery-optimization status using `PowerManager.isIgnoringBatteryOptimizations`.
Explain why background connection may be delayed. Open
`Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` only after an explicit button press;
the application remains usable if the user declines.

## MediaStore provider

Implement the existing `MEDIA_CATALOG_V1` protocol from `protocol/MEDIA_CATALOG_V1.md`:

- query `MediaStore.Images`, Video and Downloads using generation-aware pagination;
- stable item IDs are opaque content URIs plus revision metadata;
- thumbnails are generated with `ContentResolver.loadThumbnail`, bounded by the requested
  dimensions, JPEG/WebP encoded and limited to 256 KiB;
- never send an original image as a grid thumbnail;
- observe MediaStore with a `ContentObserver` and send incremental `catalog.changed`;
- stream downloads through the existing resumable file-transfer protocol;
- report permission states explicitly (`granted`, `limited`, `denied`, `revoked`).

For Android 13+ request only the media permissions needed by the selected categories. Use
the system photo picker when broad library access is unnecessary. Use the Storage Access
Framework for user-selected document trees; do not bypass scoped storage.

## File browser

Implement `protocol/FILE_BROWSER_V1.md`. Persist only SAF tree URI grants explicitly chosen
by the user. Validate every child document URI against the granted tree before list, read or
write. Uploads use a temporary sibling document and rename only after hash verification.

## Required Android tests

- service remains the sole socket owner across activity recreation and screen-off;
- manual disconnect cancels service and reconnect work;
- network loss/change produces one bounded reconnect sequence;
- boot restart respects the opt-in;
- VPN and non-VPN endpoint races close losing sockets;
- MediaStore paging, generation changes, thumbnail bounds and permission revocation;
- SAF traversal rejection, download/upload cancellation and resumable transfer hashes.

