# DeviceSync Current Architecture Audit

Date: 2026-07-15  
Scope: Windows and Android source trees, protocol documents, existing automated tests.

## Executive summary

DeviceSync is no longer an early file-transfer prototype. It already has a layered Windows application, a single-owner Android connection manager, authenticated pairing, TLS with an Android-side SPKI pin, bidirectional resumable file transfer, clipboard/text sharing, basic Android-to-Windows notifications, folder sync, persistent transfer queues, foreground background work, and a modular custom IME.

The highest-priority work is consolidation rather than adding parallel implementations:

1. bring security and protocol documents in line with the implemented TLS transport;
2. introduce per-device trust/automation policy before enabling automatic file acceptance;
3. bound queues and in-memory deduplication sets;
4. remove production debug UI and isolate diagnostics;
5. fix or quarantine the nondeterministic Android incoming-transfer test;
6. reduce Android foreground-service energy usage;
7. add protocol negotiation before media-catalog and new transports.

Both worktrees were already heavily modified before this audit. Generated Windows `bin`, `obj`, and artifact files are tracked or present in large numbers. No existing source change was reverted.

## Entry points and composition

### Windows

| Area | Current owner | Evidence |
|---|---|---|
| Process entry/lifetime | WPF `App` | `windows/src/DeviceSync.App/App.xaml.cs` |
| Dependency injection | Generic Host / Microsoft DI | `App.xaml.cs` |
| Main UI | WPF `MainWindow` + `MainViewModel` | `MainWindow.xaml`, `MainViewModel.cs` |
| Tray/single instance | `App` | `App.xaml.cs` |
| Listener | `TcpDeviceServer` | `windows/src/DeviceSync.Infrastructure/TcpDeviceServer.cs` |
| One active session/read loop | `ClientSession` | `windows/src/DeviceSync.Infrastructure/ClientSession.cs` |
| TLS server identity | `WindowsDeviceIdentityKeyProvider` through `ITlsCertificateProvider` | DI in `App.xaml.cs` |
| Pairing/auth | `PairingRequestHandler`, `AuthHandshakeHandler` | Application layer |
| Trust storage | JSON repository in LocalAppData | `FileTrustedDeviceRepository.cs` |
| Feature routing | `FeatureMessageTransport` | Infrastructure layer |
| Incoming/outgoing files | Application managers | `IncomingFileTransferManager.cs`, `OutgoingFileTransferManager.cs` |
| Clipboard/text | `SharingManager` | Application layer |
| Notifications | `NotificationManager` | Application layer |
| Folder sync | `FolderSyncManager` | Application layer |

DI mostly respects the intended Protocol → Application → Infrastructure → App direction. `MainViewModel` is currently a large integration surface and exposes raw server/discovery details in the primary UI.

### Android

| Area | Current owner | Evidence |
|---|---|---|
| Process/application graph | `DeviceSyncApplication` and its container | `app/src/main/java/com/example/devicesync/DeviceSyncApplication.kt` |
| Main UI | Compose + Navigation | `DeviceSyncApp.kt`, `navigation/DeviceSyncNavHost.kt` |
| Socket/session owner | `ConnectionManager` | `core/network/ConnectionManager.kt` |
| Socket implementation | `TcpDeviceConnection` | `core/network/TcpDeviceConnection.kt` |
| Pairing/auth | `DefaultPairingCoordinator`, `PairingProtocol` | `core/security` |
| Identity keys | Android Keystore | `AndroidKeystoreDeviceIdentityKeyProvider.kt` |
| Trust storage | Room | `RoomTrustedDeviceRepository.kt`, `TrustedDeviceEntity.kt` |
| Files | managers + persistent queue/checkpoints | `core/transfer` |
| Clipboard/text | `SharingManager` | `core/sharing` |
| Notifications | `NotificationListenerService` + allowlist | `core/notifications/NotificationForwarding.kt` |
| Folder sync | `FolderSyncManager` | `core/foldersync` |
| Background connection | foreground service + workers | `core/background/TransferBackgroundServices.kt` |
| Keyboard | separate engine and IME modules | `keyboard-engine`, `keyboard-ime` |

`ConnectionManager` is the only normal-session socket reader. File transfer, sharing, notification, and folder components receive routed messages instead of reading the socket. The writer uses a buffered channel and a single writer coroutine.

## Transport and security

### Implemented

- Windows wraps accepted TCP clients in `SslStream` and enables TLS 1.2/1.3.
- Android creates an `SSLSocket`, performs a handshake, and validates the server public-key SPKI fingerprint.
- The pairing QR contains the TLS server SPKI fingerprint; pairing itself sets the pin before connecting.
- Long-lived ECDSA identities, signed auth transcripts, fresh nonces, trust revocation, and constant-time fingerprint comparison exist.
- Normal features are routed only after authenticated session establishment.
- Protocol frames have maximum JSON-size validation on both platforms.

### Documentation drift

Before this audit, `protocol/TLS_PINNING_PLAN.md`, `PAIRING_V1.md`, and `AUTH_V1.md` still described plaintext transport. This contradicts the current code and tests.

### Security and reliability risks

| Priority | Finding | Location / consequence |
|---|---|---|
| P0 | Trust records contain identity but no per-device permissions for clipboard, automatic files, notifications, or network scope. | Both trusted-device models; automation cannot be authorized safely per device. |
| P1 | Windows outgoing session queue is unbounded. | `ClientSession`: `Channel.CreateUnbounded`; a slow peer can grow memory usage. |
| P1 | Clipboard revision sets grow for the process lifetime. | Both `SharingManager` implementations. |
| P1 | Android pending clipboard text is stored as plaintext in ordinary SharedPreferences. | `SharingPreferences`; sensitive text can persist after send failure. |
| P1 | Android foreground service holds a partial WakeLock and high-performance WifiLock for its full lifetime. | `ConnectionForegroundService`; severe battery risk. |
| P1 | Android full unit suite has a nondeterministic incoming-file test. | Full run failed byte comparison; isolated rerun passed. |
| P1 | Strict protocol-version equality prevents graceful negotiation. | `ProtocolConstants.ProtocolVersion = 1`, handshake checks. |
| P1 | Basic notification payloads can expose message text after allowlisting; lock-screen/device policy is absent. | `NotificationForwarding.kt`, `NotificationManager.cs`. |
| P2 | Production Android pairing screen contains `DebugPairingInfo` and logcat-oriented text. | `PairingVerificationScreen.kt`. |
| P2 | Windows main screen exposes port/discovery internals and utility-style controls. | `MainWindow.xaml`. |
| P2 | Windows `FolderSyncManager` uses an `async void` message handler. | Exceptions are harder to contain/test. |
| P2 | Android logs include host/port and pairing target lists. | Gate/redact in release support logs. |
| P2 | Security docs and code were not updated atomically. | Future changes can rely on false assumptions. |

No path was found where a feature manager competes with `ConnectionManager`/`ClientSession` for the same normal-session input stream.

## Feature status

| Feature | Status | Notes |
|---|---|---|
| Pairing and trusted identities | Implemented | QR, verification, persistence, revoke/delete. |
| TLS + Android SPKI pinning | Implemented, docs stale | Integration tests exist. |
| Authenticated reconnect | Implemented | Signed challenge/response and heartbeat. |
| Automatic reconnect | Implemented/needs real-device hardening | OEM constraints remain. |
| Clipboard text | Implemented, partial policy | Opt-in, manual mode, revision dedup, retry; no per-device policy. |
| Text share | Implemented | Android share target and recent in-process history. |
| Bidirectional file transfer | Implemented | Streaming, SHA-256, explicit decision, V2 resume. |
| Transfer queue/resume | Implemented | Persistent queues/checkpoints and chunk acknowledgements. |
| Automatic file acceptance | Absent by design | Must wait for trust policy. |
| Folder sync | Implemented/experimental | Manifest/plan/approval and conflict-copy logic. |
| Android → Windows notifications | Basic implementation | Allowlist, posted/removed, tray/history; no actions or rich policy. |
| Media/file catalogue | Absent | No MediaStore catalogue or Windows gallery. |
| Edge-panel Windows UI | Absent | Current UI is a conventional utility window. |
| Product Android redesign | Partial | Compose exists; debug/placeholder content remains. |
| Alternate transports/Bluetooth | Absent | LAN TCP only. |
| Keyboard IME | Implemented/experimental | RU/EN, T9, clipboard, emoji, translator UI, settings. |
| T9 morphology/context V2 | Absent | Current prefix/correction engine is a baseline. |
| Local keyboard translation | Implemented but not validated end-to-end | Reported unreliable. |
| Observability/support bundle | Partial | Technical logs exist; no unified redacted support bundle. |
| Release packaging | Partial | Artifacts exist; no clean reproducible release proof. |

## Recommended module boundaries

Do not rewrite the project. Evolve these seams:

1. Add a shared application-level `DeviceAuthorizationPolicy` abstraction backed by per-device policy storage on both platforms.
2. Replace feature-specific global booleans with policy queries keyed by authenticated device ID and action.
3. Keep `ClientSession`/`ConnectionManager` as sole socket readers; put version negotiation in handshake collaborators.
4. Introduce transport abstraction only after LAN behavior is covered by contract tests.
5. Build media catalogue as a new routed application feature with bounded pagination and thumbnails.
6. Split Windows `MainViewModel` by feature before implementing the edge panel.
7. Move Android diagnostics to a dedicated screen/build type before redesigning main navigation.
8. Keep the IME independent from connection lifecycle.

## Recommended migration order

1. Baseline and trust policy (P01–P02).
2. Reconcile and verify existing TLS hardening (P03), not a second TLS stack.
3. Protocol negotiation and bounded queues/dedup stores (P04).
4. Product UI foundations (P05–P08).
5. Per-device automatic clipboard/files (P09–P10).
6. Background energy/reliability changes (P11).
7. Media catalogue, alternate transports, and notifications refinements.

