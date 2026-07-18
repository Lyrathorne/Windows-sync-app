# DeviceSync Auth V1

Authenticated reconnect uses the existing TCP frame format:

```text
[4-byte big-endian length][JSON UTF-8 ProtocolMessage]
```

Auth V1 runs inside the pinned TLS transport. TLS protects confidentiality and
binds the Windows transport key; the signed Auth V1 transcript additionally
authenticates the long-lived DeviceSync identities. There is no plaintext
fallback.

## Message Order

```text
connection.hello
auth.challenge
auth.response
auth.accepted
normal session traffic
```

Heartbeat and pending application messages are allowed only after `auth.accepted`.

Authentication establishes identity, not blanket automation permission. Each
automatic feature must also pass the per-device policy in
`../docs/TRUST_AND_AUTOMATION_POLICY.md`.

## Hello Additions

`connection.hello` must identify the Android trust record and include a fresh 32-byte `clientNonce`, the Android identity fingerprint, and `authVersion`.

## Challenge

Windows looks up the Android trusted-device record. Only active, non-revoked records may continue.

Failures are fatal:

```text
PAIRING_REQUIRED
TRUST_REVOKED
IDENTITY_KEY_CHANGED
```

Revocation policy:

```text
Revoked trust record exists -> TRUST_REVOKED
No trust record exists -> PAIRING_REQUIRED
```

Both outcomes close the TCP connection before an authenticated session is created.

`auth.challenge` contains:

```json
{
  "serverNonce": "...",
  "windowsIdentityFingerprint": "...",
  "serverSignature": "...",
  "helloMessageId": "..."
}
```

The signature covers the canonical Auth V1 transcript:

```text
DeviceSyncSessionAuthV1
protocolVersion
androidDeviceId
windowsDeviceId
androidFingerprint
windowsFingerprint
clientNonce
serverNonce
helloMessageId
```

## Response

Android verifies the pinned Windows fingerprint and signature, then sends:

```json
{
  "helloMessageId": "...",
  "clientSignature": "..."
}
```

Windows verifies the same transcript against the trusted Android public key and sends `auth.accepted`.

## Replay Protection

Every reconnect uses a new Android client nonce, a new Windows server nonce, and the hello message ID inside the signed transcript. Replayed signatures from previous attempts must not verify for a new attempt.
