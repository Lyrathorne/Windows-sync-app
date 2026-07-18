# Bluetooth transport evaluation

Date: 2026-07-16

## Candidates

| Criterion | Classic RFCOMM | BLE GATT / L2CAP |
|---|---|---|
| Programming model | Bidirectional stream | Characteristics, notifications, negotiated MTU |
| Android | Native `BluetoothSocket` RFCOMM | Native GATT; background APIs are stronger |
| Windows desktop | RFCOMM listener/client through 32feet or packaged WinRT | WinRT GATT APIs; desktop packaging and role support add complexity |
| Throughput | Suitable for commands and small files | Usually lower and sensitive to MTU/notification pacing |
| Framing integration | Reuses existing stream frame reader/writer | Requires fragmentation, reassembly, sequence ACKs, and GATT backpressure |
| Background | Requires a live process/foreground connected-device service on Android | Companion Device / pending-intent discovery options are better |
| Compatibility | Requires Bluetooth Classic and system bonding | Requires compatible central/peripheral roles and GATT service |

## Decision

DeviceSync uses **Bluetooth Classic RFCOMM** as a slow fallback. The existing protocol is stream-oriented, so RFCOMM has substantially lower implementation and interoperability risk than GATT.

Official references:

- [Android Bluetooth overview](https://developer.android.com/develop/connectivity/bluetooth)
- [Android Bluetooth permissions](https://developer.android.com/develop/connectivity/bluetooth/bt-permissions)
- [Android background Bluetooth guidance](https://developer.android.com/develop/connectivity/bluetooth/ble/background)
- [Windows RFCOMM guidance](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/send-or-receive-files-with-rfcomm)
- [Windows Bluetooth API selection FAQ](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/bluetooth-dev-faq)

## Security

System Bluetooth bonding is not considered DeviceSync authentication.

The RFCOMM stream is wrapped in pinned TLS:

- Windows hosts TLS with the persistent DeviceSync certificate through `SslStream`.
- Android uses Bouncy Castle TLS over the Bluetooth input/output streams.
- Android validates the same persisted Windows SPKI fingerprint learned during DeviceSync pairing.
- DeviceSync Auth V1 then authenticates both saved application identities.
- There is no plaintext fallback.

## Limits

- maximum file: 2 MiB;
- transfer chunk: 24 KiB before Base64;
- bounded writer queues and normal protocol timeouts provide backpressure;
- media catalog, thumbnails, folder sync, and File Transfer V2 are disabled;
- clipboard, shared text, commands, notifications, and small File Transfer V1 payloads remain allowed.

## User flow

1. Pair DeviceSync normally so the application identities and TLS pin are trusted.
2. Pair the phone and computer in their system Bluetooth settings.
3. On Android, open Add device → Bluetooth fallback and grant Nearby devices.
4. Select the matched bonded computer.
5. Both applications label the session as a slow secure Bluetooth fallback.

LAN/USB/hotspot remains preferred. Bluetooth is attempted only explicitly or after repeated reconnect failures when a bonded fallback endpoint was previously selected.

## Dependencies

- Windows: `InTheHand.Net.Bluetooth` 4.2.4, MIT.
- Android: Bouncy Castle `bcprov-jdk18on` and `bctls-jdk18on` 1.84 under the Bouncy Castle licence.

Attribution and dependency licences must remain included in release notices.
