# DeviceSync TLS identity lifecycle

## Identity format

Windows owns one long-lived ECDSA P-256 key. Its PKCS#8 private-key bytes are protected with Windows DPAPI for the current user and stored atomically in:

`%LOCALAPPDATA%\DeviceSync\Security\identity-key.bin`

The self-signed TLS certificate is generated from that same key. DeviceSync pins the SHA-256 fingerprint of the certificate Subject Public Key Info (SPKI), not the short-lived certificate bytes. Regenerating a certificate with the same key therefore keeps the trusted pin stable.

The pairing QR contains both the application identity fingerprint and the TLS SPKI fingerprint. Android stores the TLS fingerprint in the trusted-device record and installs it before opening either a pairing or a normal connection.

## Connection security boundary

1. Android refuses to open a connection when the trusted record has no TLS pin (`PAIRING_REQUIRED_TLS_PIN`).
2. TLS 1.2 or TLS 1.3 must complete and the server SPKI must match the stored pin.
3. Auth V1 then verifies the signed DeviceSync identities inside the encrypted channel.
4. Only after both checks succeed may clipboard, file, notification, or media messages be routed.

There is no plaintext compatibility path. A TLS failure closes the socket; it never retries without TLS.

## Restart and migration

- A normal Windows restart reloads the protected key, so the identity and TLS pin remain unchanged.
- A legacy Android trusted record without `futureTlsCertificateFingerprint` is not upgraded silently. The connection fails with `PAIRING_REQUIRED_TLS_PIN` and the user must pair again using a fresh QR code.
- An unexpected application identity key produces `IDENTITY_KEY_CHANGED`. A mismatched TLS identity fails earlier as `TLS_PIN_MISMATCH`. Both are security events, not ordinary reconnect failures.
- Corrupt protected-key data is never overwritten automatically. Windows reports an identity-storage failure so a transient disk or DPAPI problem cannot silently replace a trusted identity.

## Explicit recovery and rotation

If the Windows key is irrecoverably lost or intentionally reset:

1. Stop DeviceSync on Windows.
2. Use the explicit identity reset/recovery action; do not manually introduce a replacement while sessions are active.
3. The reset removes the old protected key and all existing sessions must be considered untrusted.
4. Remove the old Windows device from Android and the old Android trust entry from Windows.
5. Start Windows, generate a new identity, and perform pairing again in person using the new QR code.
6. Re-enable per-device automation permissions explicitly; identity trust alone must not restore them.

DeviceSync V1 does not support transparent key rotation. Treat every changed identity as replacement of the device.

## Verification checklist

Automated tests cover:

- stable identity and TLS SPKI across provider instances and file-backed restarts;
- corrupt key storage is not replaced silently;
- a pinned TLS connection succeeds;
- a wrong TLS pin/certificate is rejected;
- Android refuses to connect when a migrated trust record has no pin.

Release packet-capture check (manual, required before release):

1. Pair clean installations and start a TLS-authenticated session.
2. Capture the DeviceSync TCP port with Wireshark.
3. Send a unique clipboard marker and a file containing another unique marker.
4. Search packet bytes for both markers and for JSON field names such as `messageId`, `file.chunk`, and `clipboard.update`.
5. The capture may show TLS records and traffic sizes, but none of those plaintext markers may be present.
6. Repeat with an intentionally wrong pin and confirm that no application protocol frame is sent.

Packet capture is an external release verification and is not claimed by unit tests.
