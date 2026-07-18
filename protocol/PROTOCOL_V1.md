# DeviceSync Protocol v1

This document describes the protocol implemented by the Android app in `C:\Users\Gleb\Documents\Android sync app` and by the Windows app in this repository.

## Framing

Every message is sent as:

```text
[4 byte JSON length][JSON UTF-8 payload]
```

The length is a signed 32-bit integer in big-endian byte order. It is the number of UTF-8 bytes in the JSON payload, not the number of characters.

Valid JSON frame-body sizes are `1..1048576` bytes. The `payload` JSON value is limited to `983040` UTF-8 bytes. See [PROTOCOL_NEGOTIATION_V1.md](PROTOCOL_NEGOTIATION_V1.md) for negotiated limits and migration rules.

## JSON

Messages use UTF-8 JSON, camelCase property names, no pretty printing, and no comments or trailing commas. Unknown fields are ignored while reading.

The envelope is:

```json
{
  "protocolVersion": 1,
  "messageId": "unique-message-id",
  "type": "connection.hello",
  "senderDeviceId": "android-device-id",
  "recipientDeviceId": null,
  "timestampUtc": "2026-07-05T18:45:00Z",
  "correlationId": null,
  "originDeviceId": null,
  "requiresAcknowledgement": false,
  "payload": {}
}
```

Timestamps are UTC ISO-8601 strings.

## Message Types

`connection.hello`
`connection.hello_ack`
`connection.ping`
`connection.pong`
`connection.close`
`message.ack`
`error.protocol`

## Capabilities

Capability names, implemented/reserved status, and explicit refusal rules are defined in [PROTOCOL_NEGOTIATION_V1.md](PROTOCOL_NEGOTIATION_V1.md). A peer advertises only implemented features.

## Payloads

`connection.hello` payload:

```json
{
  "deviceName": "Pixel",
  "deviceType": "android",
  "appVersion": "1.0",
  "protocolVersion": 1,
  "protocolMin": 1,
  "protocolMax": 1,
  "maxFrameBytes": 1048580,
  "maxPayloadBytes": 983040,
  "capabilities": ["heartbeat-v1", "ack-v1", "reconnect-v1"]
}
```

`connection.hello_ack` payload:

```json
{
  "deviceName": "Windows PC",
  "deviceType": "windows",
  "acceptedProtocolVersion": 1,
  "protocolMin": 1,
  "protocolMax": 1,
  "maxFrameBytes": 1048580,
  "maxPayloadBytes": 983040,
  "capabilities": ["heartbeat-v1", "ack-v1", "reconnect-v1"]
}
```

`connection.ping` payload:

```json
{
  "sequence": 1,
  "sentAtUtc": "2026-07-05T18:45:02Z"
}
```

`connection.pong` payload:

```json
{
  "sequence": 1,
  "receivedAtUtc": "2026-07-05T18:45:03Z"
}
```

`connection.close` payload:

```json
{
  "reason": "user_requested",
  "allowReconnect": true
}
```

`message.ack` payload:

```json
{
  "status": "ok",
  "errorCode": null,
  "errorMessage": null
}
```

`error.protocol` payload:

```json
{
  "code": "invalid_message",
  "message": "The message is invalid.",
  "fatal": true
}
```

## Handshake

Android opens a TCP connection to the Windows server and sends `connection.hello` first. Windows rejects the connection if the first message is not `connection.hello`, if protocol version is not `1`, if `senderDeviceId` is empty, if the hello payload has an empty `deviceName`, or if `deviceType` is not `android`.

Windows replies with `connection.hello_ack`. The ACK has:

`senderDeviceId`: persistent Windows ID in `windows-<uuid>` format.
`recipientDeviceId`: Android sender device ID.
`correlationId`: Android hello `messageId`.

## Heartbeat

When Windows receives `connection.ping`, it replies with `connection.pong`. Pong keeps the same `sequence`, sets `correlationId` to the ping `messageId`, and swaps sender/recipient device IDs.

## Close

Either side may send `connection.close`. Windows closes only that client session and keeps the TCP server listening.

## Compatibility Rules

New fields may be added to payloads if old readers can ignore them. Existing message type strings, envelope field names, frame byte order, and maximum JSON size must not change inside protocol version 1.
## Transport capabilities

The logical DeviceSync protocol is transport-independent. Current local transport capabilities are:

- `transport-lan-tls-v1`;
- `transport-hotspot-v1`;
- `transport-usb-v1`;
- `transport-bluetooth-v1`.

Every transport must establish pinned TLS before the DeviceSync Auth V1 exchange. A transport switch creates a fresh authenticated connection; File Transfer V1 and other non-resumable operations fail explicitly instead of being silently continued on another stream. Only one reader may own a logical device session at a time, and repeated message IDs must not be routed twice.

Bluetooth is a constrained profile: maximum file size 2 MiB, 24 KiB file chunks, bounded writer queues, and no media catalog, thumbnails, folder sync, or File Transfer V2.
