# LAN, hotspot, and USB connectivity

DeviceSync supports local-only connections without an Internet relay:

- both devices on the same Wi-Fi or Ethernet LAN;
- Android hotspot with Windows joined to it;
- Windows Mobile Hotspot with Android joined to it;
- USB tethering that exposes a private Ethernet interface.

## Discovery

Automatic discovery combines:

- DNS-SD/mDNS `_devicesync._tcp`;
- a small `DeviceSyncLanBeaconV1` UDP broadcast on port `54322`;
- remembered paired endpoint;
- pairing QR addresses;
- manual IP and port entry.

The Windows publisher restarts when network addresses change. Android merges DNS-SD and UDP results by stable device ID and removes stale beacon results.

## Address policy

- Private IPv4 and IPv6 link-local candidates are accepted.
- Loopback, multicast, and unspecified addresses are rejected.
- VPN, WireGuard, OpenVPN, and Tailscale adapters are excluded from automatic selection.
- USB RNDIS and Windows hotspot adapters are classified separately and receive appropriate transport priority.
- Diagnostics show only transport classes, counts, and hashed interface identifiers—not MAC addresses or a complete list of user addresses.

## Captive portals and multiple adapters

Android diagnostics reports captive portal, VPN presence, metered status, and transport classes. A network-handle change terminates the old socket and starts a fresh authenticated connection. DeviceSync never treats Internet availability as a requirement; a private local route is sufficient.

## Firewall

Windows must allow inbound local-network traffic for:

- TCP `54321` (or the configured DeviceSync port);
- UDP `5353` for mDNS;
- UDP `54322` for the fallback beacon.

The application does not silently weaken firewall policy. If discovery is blocked, QR/manual/remembered endpoints continue to work as long as the TCP rule permits the connection.
