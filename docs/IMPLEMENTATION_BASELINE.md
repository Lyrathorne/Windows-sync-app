# DeviceSync Implementation Baseline

Date: 2026-07-15

## Repository state

Both worktrees contain extensive pre-existing modified/untracked source, generated output, and artifact directories. This baseline does not clean, reset, stage, or commit them.

Windows `bin`, `obj`, and published artifacts appear in `git status`. They should be handled in a dedicated repository-hygiene task after confirming which binaries the user intentionally retains.

## Verified commands

### Windows

```powershell
dotnet test windows\DeviceSync.sln --no-restore --logger "console;verbosity=minimal"
```

Result: **passed** — 114 tests, 0 failures, 0 warnings across:

- `windows/tests/DeviceSync.Protocol.Tests`
- `windows/tests/DeviceSync.Application.Tests`
- `windows/tests/DeviceSync.IntegrationTests`

### Android

```powershell
$env:JAVA_HOME='C:\Program Files\Android\Android Studio\jbr'
.\gradlew.bat :keyboard-engine:test :keyboard-ime:testDebugUnitTest :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
```

Full combined result: **failed** in `:app:testDebugUnitTest` after 105 app tests:

```text
IncomingFileTransferManagerTest.acceptedFileIsStreamedVerifiedAndAcknowledged
ArrayComparisonFailure: saved bytes did not match expected bytes
```

The same test passed when run alone immediately afterwards:

```powershell
.\gradlew.bat :app:testDebugUnitTest --tests "com.example.devicesync.core.transfer.IncomingFileTransferManagerTest.acceptedFileIsStreamedVerifiedAndAcknowledged" --info
```

Classification: **flaky/nondeterministic baseline**. Likely investigation areas are coroutine scheduling, listener lifecycle, and test isolation. It must be reproduced and fixed before the full suite becomes a release gate.

Build and lint passed independently:

```powershell
.\gradlew.bat :app:lintDebug :app:assembleDebug
```

Current debug APK:

```text
C:\Users\Gleb\Documents\Android sync app\app\build\outputs\apk\debug\app-debug.apk
```

## Baseline invariants

- Windows accepts TLS 1.2/1.3 and does not intentionally fall back to plaintext.
- Android requires the pinned Windows SPKI fingerprint for normal and pairing connections.
- Signed application authentication runs inside TLS and binds long-lived DeviceSync identities.
- Exactly one normal-session reader exists per connection on each platform.
- File data is streamed rather than intentionally loaded in full.
- Incoming files use temporary storage/checkpoints, checksum validation, and verified finalization.
- Clipboard content is limited to 256 KiB and is disabled by default.
- Notification forwarding requires opt-in and an app allowlist.
- Android background connection is opt-in but currently holds long-lived runtime locks.
- Protocol version is fixed at V1; capabilities exist but version-range negotiation does not.

## Release blockers visible at baseline

1. Android flaky unit test.
2. Per-device authorization policy absent.
3. Protocol/security documentation drift.
4. Unbounded Windows session queue and clipboard revision sets.
5. Energy-heavy Android locks.
6. Production debug UI and inconsistent localization.
7. No clean release/installer pipeline or clean-worktree reproducibility proof.

