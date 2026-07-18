# DeviceSync TLS Pinning Plan

Status: implemented and covered by persistence, corruption, and pin-rejection tests.

Current implementation:

1. Windows creates and persists a self-signed TLS certificate through the Windows identity key provider.
2. Pairing QR includes the Windows TLS public-key SPKI SHA-256 fingerprint.
3. Android pins that SPKI before the pairing connection and stores it in the trusted-device record.
4. Windows accepts TLS 1.2/1.3 with `SslStream`; Android uses `SSLSocket` and rejects a mismatched pin.
5. DeviceSync application identity is mutually authenticated by the signed Auth V1 transcript inside TLS.
6. Normal and pairing sessions do not intentionally fall back to plaintext.
7. Integration tests cover TLS success and bad-pin rejection.

Operational lifecycle, legacy-record migration, explicit recovery, rotation policy, and the manual packet-capture release check are documented in `../docs/TLS_IDENTITY_LIFECYCLE.md`.

Future transport variants must preserve this boundary: capability negotiation may select a transport, but it must never permit a plaintext downgrade. Per-device automation authorization remains separate from identity trust.

See `../docs/TRUST_AND_AUTOMATION_POLICY.md`.
