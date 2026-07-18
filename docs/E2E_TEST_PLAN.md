# DeviceSync end-to-end and fault-injection plan

## Automated coverage currently present

- Protocol JSON vectors and capability negotiation on Android and Windows.
- Authentication, TLS identity pinning and invalid identity rejection.
- Reconnect backoff and network-transition policies.
- Clipboard deduplication and echo-loop prevention.
- Incoming/outgoing file state machines, checksum mismatch, cancellation, disconnect and `.part` cleanup.
- Loopback file transfer integration tests.
- Media catalog pagination, thumbnails, permission revocation and downloads.
- Notification mapping, filtering, action allowlist and deduplication.
- Transport selection and Bluetooth capability downgrade.
- Privacy-safe diagnostic redaction.

Run before every release:

```powershell
# Windows
dotnet test windows/DeviceSync.sln -c Release

# Android
gradlew.bat testReleaseUnitTest assembleRelease
gradlew.bat compileDebugAndroidTestKotlin
```

## Deterministic fault cases

| Fault | Injection point | Required result |
|---|---|---|
| 100–1000 ms latency | framed transport wrapper | UI stays responsive; heartbeats tolerate configured threshold |
| disconnect after N frames | reader/writer wrapper | one termination path; reconnect starts once |
| duplicate message | protocol dispatcher | acknowledgement/idempotency prevents repeated side effect |
| reordered sequential chunk | file transport fake | stable index/offset error; partial removed |
| corrupted chunk | file transport fake | SHA-256 mismatch; no final file |
| capability removed | hello/ack test vector | feature hidden/rejected without session failure |
| Wi-Fi interface switch | network monitor fake | authenticated fresh session; stale reader cannot mutate state |
| low disk | storage abstraction fake | offer rejected or transfer fails before final rename |
| permission revoked mid-catalog | catalog source fake | stable permission error and active operation cancellation |

## Manual release gate

The automated suite must be green, then every applicable row in
`docs/REAL_DEVICE_TEST_MATRIX.md` must be explicitly marked. Unrun physical-device rows remain
visible and prevent declaring P27 fully complete.
