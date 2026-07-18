# DeviceSync protocol negotiation and evolution

## Compatibility envelope

The length-prefixed UTF-8 JSON envelope remains protocol version 1 while the current range is negotiated. A peer sends `protocolVersion: 1` in the envelope and advertises its application-protocol range inside `connection.hello`:

```json
{
  "protocolVersion": 1,
  "protocolMin": 1,
  "protocolMax": 1,
  "maxFrameBytes": 1048580,
  "maxPayloadBytes": 983040
}
```

`protocolVersion` in the payload is the legacy single-version field. `protocolMin` and `protocolMax` are optional so an older V1 peer remains compatible. If either range field is absent, both bounds fall back to the legacy value.

The selected version is the highest value in the intersection of both ranges. No intersection produces `UNSUPPORTED_PROTOCOL_VERSION`; the connection closes without trying another, weaker transport or plaintext.

The server returns `acceptedProtocolVersion`, its range, limits, and capabilities in `connection.hello_ack`. Authenticated sessions carry the accepted version and server capabilities in `auth.challenge` and `auth.accepted`, so the security handshake and the non-authenticated test handshake use the same result.

## Limits

- Maximum JSON frame body: 1,048,576 UTF-8 bytes.
- Frame header: 4 bytes, so maximum wire frame: 1,048,580 bytes.
- Maximum `payload` JSON value: 983,040 UTF-8 bytes.
- The effective peer limit is the smaller advertised value, but a local implementation must never exceed its own hard limit.
- File data remains chunked; limits are not permission to load a whole file into memory.

Oversized frames are rejected by the frame reader/writer. Oversized payloads are rejected with `PAYLOAD_TOO_LARGE` before routing.

## Capabilities

Capabilities are exact, case-sensitive strings. The negotiated set is the intersection of local and remote advertised values. A feature must fail explicitly with `CAPABILITY_REQUIRED:<capability>` when its required capability is absent.

Currently advertised capabilities include implemented features and `transport-lan-tls-v1`. The following reserved constants are defined but must not be advertised until their implementation and trust controls are complete:

- `clipboard-auto-v1`
- `file-auto-receive-v1`
- `media-catalog-v1`
- `thumbnails-v1`
- `transport-hotspot-v1`
- `transport-usb-v1`
- `transport-bluetooth-v1`

`notifications-v1` remains the capability for the existing notification protocol.

## Stable error codes

- `UNSUPPORTED_PROTOCOL_VERSION`
- `CAPABILITY_REQUIRED`
- `PAYLOAD_TOO_LARGE`
- `INVALID_MESSAGE`
- `AUTHENTICATION_REQUIRED`

Feature protocols may define additional stable codes. User-facing text is localized separately and must not be parsed as a technical code.

## Message identity and loop prevention

- `messageId` uniquely identifies one message.
- `correlationId` links a response or acknowledgement to the initiating message.
- `originDeviceId` identifies the original producer when a message can be forwarded. It is optional for legacy messages and must not be rewritten by an intermediary.
- Feature payloads that represent mutable content use `revisionId` (clipboard V1 already does). Replayed revisions are ignored according to that feature contract.

## JSON evolution rules

- Property names use camelCase on both platforms.
- Unknown object fields are ignored.
- New optional fields must have safe defaults.
- A field cannot change JSON type or meaning within a protocol version.
- Required fields cannot become optional unless a documented legacy fallback exists.
- Nullable values may be omitted; receivers must accept either omission or JSON `null` for optional fields.
- Byte counts and file sizes use signed 64-bit `long`/`Long` where they may exceed 2 GiB.

The canonical compatibility vector is `test-vectors/negotiation/connection-hello-v1.json`. Windows and Android tests both deserialize the same bytes (the Android test resource is a synchronized copy for its separate repository build).

## Migration beyond V1

1. Add all new fields as optional and ship readers before writers depend on them.
2. Define the new version and capability in both protocol packages and shared vectors.
3. Increase `protocolMax` only after both implementations can parse and safely reject the new feature.
4. Continue using the V1 envelope for negotiation while it can represent the new range.
5. Send new message types only after the capability intersection confirms support.
6. Keep V1 behavior available while V1 is inside the supported range; never emulate a missing security capability.
7. Remove V1 only in a major migration with explicit user-visible upgrade/re-pair guidance.

Negotiation is protocol/application logic and has no UI dependency.
