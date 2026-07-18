# DeviceSync Trust and Automation Policy

Status: approved product/security specification for implementation  
Version: 1  
Date: 2026-07-15

## Purpose

Pairing proves which DeviceSync installation is on the other end. It does not grant every future automation permission. DeviceSync separates:

- **identity trust** — a public key belongs to the paired installation;
- **transport security** — the session uses pinned TLS and authenticated identities;
- **feature authorization** — this device may perform a specific automatic action.

All automatic actions MUST pass all three checks.

## Trusted-device lifecycle

1. `Pending` — pairing is valid but final activation is incomplete. No normal features.
2. `Active` — identity may establish authenticated sessions.
3. `SecurityReviewRequired` — identity/certificate changed or migration is ambiguous. Automatic features are blocked.
4. `Revoked` — connection and all features are rejected.
5. `Deleted` — trust and policy are removed; new pairing is required.

An active device is accepted only when the TLS SPKI pin matches, the application signature matches the stored identity, the record is active/non-revoked, and protocol capabilities permit the feature.

Changing display name is not an identity change. Changing the public identity key or unexpected TLS SPKI is a security event and MUST NOT be silently accepted.

## Authorization formula

```text
global feature setting
AND per-device permission
AND current network scope
AND current device/privacy state
AND negotiated capability
AND feature-specific validation
```

### Per-device values

- `Deny` — blocked.
- `Ask` — explicit decision for each action.
- `AllowTrustedNetwork` — automatic on a user-approved local network profile.
- `AllowAnyEncryptedNetwork` — automatic on any pinned TLS + authenticated session.

Global settings never upgrade a per-device `Deny` or `Ask`.

### Network profiles

A trusted network is explicitly approved. SSID alone is not a security identity and must not be the only check. A profile may include a platform network identifier plus gateway/subnet observations. If it cannot be verified, policy falls back to `Ask`.

Hotspot and USB tethering are unclassified until approved. Bluetooth has a separate transport permission and does not inherit LAN trust.

## Safe defaults

| Feature | Default after pairing | Notes |
|---|---|---|
| Manual text/file send | Allowed after authenticated connection | User initiated; receiver validation remains. |
| Automatic clipboard | Off globally; per-device `Deny` | Both settings must be enabled. |
| Automatic incoming files | `Ask` | Never automatically open/execute. |
| Notifications Android → Windows | Off; empty app allowlist | Secret notifications remain blocked. |
| Media catalogue/thumbnails | Off until Android collection permission and per-device approval | Originals download explicitly. |
| Folder sync | `Ask` per plan | Roots and conflict handling are explicit. |
| Remote notification actions | `Deny` | Requires later security review. |

## Clipboard policy

- Text and HTTP(S) URLs only in V1, maximum 256 KiB UTF-8.
- Empty clipboard never overwrites a non-empty remote clipboard.
- `revisionId`/`originDeviceId` prevent loops; dedup retention is bounded and expiring.
- Password, payment, no-personalized-learning, and IME incognito contexts are excluded.
- Android background clipboard limitations must be stated honestly.
- Pending failed clipboard data is encrypted at rest or volatile, bounded, and short-lived.
- Received URLs are never opened automatically.

Recommended text:

> Automatically synchronize copied text with **{deviceName}** while an encrypted trusted connection is active. Password fields and private keyboard mode are excluded.

## Incoming-file policy

Per-device modes:

- `AlwaysAsk` — default;
- `AutoSafeFilesTrustedNetwork`;
- `AutoSafeFilesAnyEncryptedNetwork`;
- `NeverReceive`.

Limits:

- protocol maximum: 100 MiB;
- recommended default automatic limit: 25 MiB;
- one active transfer per connection unless a later capability is negotiated.

Initial automatic allowlist: non-executable common images, text, PDF, and explicitly selected document types. Executables, scripts, shortcuts, installers, macro-enabled documents, suspicious archives, and unknown MIME/extension combinations require confirmation or are denied. MIME and extension are checked together.

All modes retain safe basename/reserved-name checks, destination containment, free-space validation, unique non-overwriting names, `.part` storage, streaming hash verification, cleanup, and no automatic opening.

Recommended warning:

> Automatically received files can contain unsafe content. DeviceSync verifies integrity and blocks common executable types, but cannot guarantee that every document is harmless.

## Notifications policy

- Disabled globally by default.
- Per-app allowlist.
- `VISIBILITY_SECRET` never forwarded.
- Private content hidden by default when either device is locked.
- Text is not persisted unless history is explicitly enabled.
- No raw `PendingIntent`, `RemoteViews`, tokens, or arbitrary extras cross the connection.
- Remote actions and URLs require explicit user action and a future capability.
- Rate limits and deduplication apply per source app.

## Device and privacy states

| State | Clipboard | Auto files | Notifications | Media catalogue |
|---|---|---|---|---|
| Either device locked | Suppress by default | Finish active transfer; do not start new auto transfer | Hide content | Hide thumbnails/deny by default |
| Android work/profile policy blocks access | Deny | Deny | Deny affected apps | Deny affected collections |
| Keyboard incognito/private field | Deny automatic | User-started transfer unchanged | Unchanged | Unchanged |
| DeviceSync guest mode | Deny | Ask/deny | Deny | Deny |

## Re-pairing and identity changes

- Same device ID with a new identity is an identity replacement, not an ordinary reconnect.
- Replacement requires explicit confirmation on both devices and resets automatic permissions.
- Unexpected key/SPKI sets `SecurityReviewRequired`, disconnects, and emits a security event.
- Revocation disconnects, cancels queued automatic work, and keeps only a content-free timestamp.
- Deletion removes trust, policies, pending clipboard, per-device history, and unsafe-to-reuse resume metadata.

## Threat model

| Threat | Required mitigation |
|---|---|
| Attacker on LAN | TLS, SPKI pin, application signatures, no plaintext downgrade. |
| Spoofed discovery | Discovery is a hint; verify stored identity. |
| MITM during pairing | Short-lived QR secret, QR-carried SPKI, signed transcript, verification code. |
| Stolen unlocked laptop | Per-device toggles, lock redaction, revoke flow, no automatic opening. |
| Malicious Android app | Permission boundaries, allowlists, SAF/MediaStore, no arbitrary paths. |
| Clipboard poisoning | Opt-in, size/type/rate checks, bounded dedup, no automatic URL/command opening. |
| Path traversal/overwrite | Safe basename, containment, reserved names, unique destination, `.part`. |
| Notification leakage | Allowlist, secret/private filtering, lock policy, no content logs. |
| Replay/duplicate | Fresh auth nonces, message IDs, bounded processed-message retention. |
| Resource exhaustion | Bounded queues, frame/file limits, rate limits, timeouts, cancellation. |

## Stable policy error codes

- `TRUST_REQUIRED`
- `TRUST_REVOKED`
- `SECURITY_REVIEW_REQUIRED`
- `IDENTITY_CHANGED`
- `TLS_PIN_MISMATCH`
- `FEATURE_DISABLED`
- `DEVICE_PERMISSION_DENIED`
- `USER_CONFIRMATION_REQUIRED`
- `NETWORK_NOT_TRUSTED`
- `DEVICE_LOCKED`
- `PRIVATE_CONTEXT`
- `RATE_LIMITED`
- `CONTENT_TYPE_BLOCKED`
- `SIZE_LIMIT_EXCEEDED`

Messages name the feature/device but never expose secrets or stack traces.

## Storage and migration

Per-device policy is keyed by stable trusted-device ID and schema version. Existing records migrate to:

```text
clipboardAuto = Deny
autoReceiveFiles = AlwaysAsk
notifications = Deny
mediaCatalogue = Deny
folderSync = Ask
networkScope = Ask
```

Migration never infers per-device permission from the old global clipboard boolean. The old value may remain the global switch, but each device still requires explicit approval.
