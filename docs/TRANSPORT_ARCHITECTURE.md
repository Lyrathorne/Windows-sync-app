# DeviceSync transport architecture

## Goal

The authenticated DeviceSync session is independent from the physical channel. LAN, hotspot, USB tethering, and Bluetooth RFCOMM all feed the same protocol framing, authentication, routing, acknowledgement, and feature managers.

## Lifecycle

Each transport follows:

1. discover or load a remembered endpoint;
2. measure availability;
3. connect the byte stream;
4. establish pinned TLS;
5. run DeviceSync identity authentication;
6. attach one reader and one bounded writer to the logical session;
7. collect connection and heartbeat metrics;
8. close and report a stable termination reason.

The logical session owns device identity and negotiated capabilities. The transport owns only endpoint discovery and the secured byte stream.

## Selection and failover

Default priority:

1. USB tethering;
2. Ethernet/Wi-Fi LAN;
3. local hotspot;
4. Bluetooth RFCOMM.

Unavailable transports are skipped. Connect latency can break ties. A remembered endpoint is preferred only after transport priority and availability.

Windows uses `TransportSessionCoordinator` to guarantee that only one authenticated reader is active. A lower-priority candidate cannot replace a higher-priority session. A successful higher-priority candidate closes the previous channel.

Android reconnects through the remembered IP endpoint first. After repeated failures it may use a previously selected bonded Bluetooth endpoint. Transfer V1 is not resumed during a transport change: disconnect is delivered to feature managers, active work fails/cancels clearly, and the user can retry after reconnection.

## Duplicate delivery and backpressure

- Android persists processed message IDs.
- Windows keeps a bounded, time-limited deduplication set.
- Duplicate acknowledged messages receive a `duplicate`/processed acknowledgement and are not routed twice.
- Writers are bounded channels; producers suspend instead of allocating an unbounded queue.

## Transport profiles

| Transport | Priority | Max file | Heavy catalog |
|---|---:|---:|---|
| USB tethering | 100 | 100 MiB | enabled |
| LAN | 90 | 100 MiB | enabled |
| Hotspot | 80 | 100 MiB | enabled |
| Bluetooth RFCOMM | 10 | 2 MiB | disabled |

Bluetooth also disables thumbnails, folder sync, and File Transfer V2. File chunks are reduced to 24 KiB so Base64 JSON frames remain bounded.
