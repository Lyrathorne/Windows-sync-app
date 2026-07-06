# DeviceSync Pairing V1

Pairing V1 binds an Android installation and a Windows installation using long-lived ECDSA P-256 identity keys and a one-time QR pairing secret. The TCP transport remains plaintext in this stage.

## Algorithms

Identity key: ECDSA P-256.
Signature: SHA256withECDSA.
Pairing proof: HMAC-SHA256.
Fingerprint: SHA-256 of X.509 SubjectPublicKeyInfo DER.
Secret and nonce: 32 random bytes.
Wire encoding: Base64 URL-safe without padding.

## Message Types

`pairing.request`
`pairing.challenge`
`pairing.confirm`
`pairing.accepted`
`pairing.rejected`
`pairing.cancel`
`auth.challenge`
`auth.response`
`auth.accepted`
`auth.rejected`

## QR Payload

The Windows app displays a JSON QR payload:

```json
{
  "format": "devicesync-pairing",
  "version": 1,
  "sessionId": "pair-uuid",
  "pairingSecret": "base64url-32-bytes",
  "expiresAtUtc": "2026-07-06T12:34:56Z",
  "hostAddresses": ["192.168.1.25"],
  "port": 54321,
  "windowsDeviceId": "windows-uuid",
  "windowsDeviceName": "Gleb-PC",
  "windowsIdentityPublicKey": "base64url-spki",
  "windowsIdentityFingerprint": "base64url-sha256",
  "protocolMin": 1,
  "protocolMax": 1
}
```

The QR payload never contains private keys, passwords, user names, absolute paths, logs, or future TLS private keys. The pairing secret is short-lived, one-time, and must not be logged or persisted.

## Pairing Request Transcript

HMAC is computed over a deterministic binary transcript, not serialized JSON.

Fields:

`DeviceSyncPairingV1`
`sessionId`
`windowsDeviceId`
`androidDeviceId`
`windowsIdentityFingerprint`
`androidIdentityFingerprint`
`androidNonce`

Each field is UTF-8 with a 4-byte big-endian length prefix.

## Pairing Challenge Transcript

Fields:

`DeviceSyncPairingChallengeV1`
`sessionId`
`windowsDeviceId`
`androidDeviceId`
`windowsIdentityFingerprint`
`androidIdentityFingerprint`
`androidNonce`
`windowsNonce`

The verification code is six digits derived from SHA-256 of the challenge transcript. Leading zeros are preserved.

## Pairing Confirmation Transcript

Android signs this transcript after the user confirms the six digit code:

`DeviceSyncPairingConfirmV1`
`sessionId`
`windowsDeviceId`
`androidDeviceId`
`windowsIdentityFingerprint`
`androidIdentityFingerprint`
`androidNonce`
`windowsNonce`
`verificationCode`

The `pairing.confirm` payload contains:

```json
{
  "sessionId": "...",
  "confirmed": true,
  "androidSignature": "base64url"
}
```

## Pairing Accepted Transcript

Windows signs this transcript only after both local and remote confirmations are present:

`DeviceSyncPairingAcceptedV1`
`sessionId`
`windowsDeviceId`
`androidDeviceId`
`windowsIdentityFingerprint`
`androidIdentityFingerprint`
`androidNonce`
`windowsNonce`
`verificationCode`
`pairedAtUtc`
`permissionsCsv`

The only permissions granted by Pairing V1 are:

```text
basic_connection
heartbeat
```

The `pairing.accepted` payload contains:

```json
{
  "sessionId": "...",
  "windowsSignature": "base64url",
  "pairedAtUtc": "...",
  "permissions": ["basic_connection", "heartbeat"]
}
```

## Trust

Trusted records are saved only after:

1. pairing secret proof is valid;
2. public key fingerprints match keys;
3. verification code is confirmed by both users;
4. ECDSA signatures are verified;
5. `pairing.accepted` is sent.

Discovery TXT fingerprints are only hints. They do not replace QR or trust-store verification.
## Runtime TCP Flow

Pairing uses the normal DeviceSync TCP framing, but it is a separate short-lived session. It does not send `connection.hello`, does not start heartbeat, and does not register an authenticated device session.

```text
Android -> Windows: pairing.request
Windows -> Android: pairing.challenge
Android -> Windows: pairing.confirm
Windows -> Android: pairing.accepted
Android -> Windows: pairing.complete_ack
```

`pairing.request` and `pairing.challenge` use HMAC-SHA256 over canonical transcripts, not serialized JSON. The verification code is derived independently on both platforms and is never sent over TCP.

`pairing.complete_ack` is reserved for the durable trust commit step:

```json
{
  "sessionId": "...",
  "status": "stored"
}
```

Windows must not treat a pending trust record as active until this acknowledgement is received.
