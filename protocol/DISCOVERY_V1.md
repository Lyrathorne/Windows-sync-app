# DeviceSync Discovery V1

When more than one usable physical LAN address exists, discovery TXT records include:

- `addressCount`: number of advertised IPv4 addresses;
- `addresses`: comma-separated physical LAN IPv4 addresses in preference order;
- `endpoints`: semicolon-separated `kind|address|port` candidates.

Clients must deduplicate candidates and may race at most three endpoints. Selection is not
complete until TLS SPKI pinning and the DeviceSync identity handshake succeed. VPN, tunnel,
loopback, APIPA and unsupported virtual adapters must not be advertised. A route/interface
change invalidates the current discovery snapshot and triggers republication.

mDNS/DNS-SD is used only to discover the network address of a DeviceSync Windows service. It does not authenticate the computer and must not be treated as cryptographic trust.

## Service Type

Android searches for:

```text
_devicesync._tcp.
```

Windows publishes:

```text
_devicesync._tcp
```

The full service name on the LAN is shaped like:

```text
Gleb-PC._devicesync._tcp.local.
```

## Instance Name

The instance name is a human-readable computer name, for example `Gleb-PC`. It is not the persistent Windows Device ID. If a network name conflict is detected by a publisher implementation, the instance name may become `Gleb-PC (2)` or similar without changing `deviceId`.

## TXT Records

Required fields:

`deviceId`: claimed Windows Device ID, format `windows-<uuid>`.
`deviceName`: display name.
`deviceType`: `windows`.
`protocolMin`: minimum supported DeviceSync protocol version.
`protocolMax`: maximum supported DeviceSync protocol version.
`appVersion`: Windows app version.
`pairingAvailable`: `false` in this stage.

Optional fields:

`capabilities`: comma-separated capability strings, currently `heartbeat-v1,ack-v1,reconnect-v1`.

Unknown TXT fields must be ignored. Values longer than 255 bytes should be ignored by readers. No secrets, tokens, private keys, user files, absolute paths, or diagnostic logs may be published.

## Appearance And Loss

Android only shows a computer after `serviceFound` has been resolved successfully. `serviceLost` removes the matching temporary discovery result. Discovery results are not stored permanently.

## Address Changes

If a Windows computer changes IP address, Android updates the temporary discovery result after a successful resolve. A saved Room device host/port is updated only after a normal TCP hello handshake succeeds and the hello ACK Device ID matches the expected Device ID.

## Protocol Compatibility

Android checks whether its supported protocol version intersects with `protocolMin..protocolMax`. Incompatible computers can be shown disabled with an explanation. The TCP hello handshake remains the final protocol check.

## Manual IP Fallback

Manual IP connection remains available because mDNS can be blocked by guest Wi-Fi, client isolation, corporate networks, VPN routing, or firewall rules. Users should allow DeviceSync on a private Windows network, not disable the firewall.
