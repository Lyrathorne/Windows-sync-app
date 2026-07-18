# Notifications V1

Capability: `notifications-v1`.

Notifications V1 mirrors selected Android notifications to an authenticated Windows
device. Android owns the notification and Windows keeps only a bounded, in-memory
view of the current connection session.

## Messages

| Type | Direction | Purpose |
|---|---|---|
| `notification.posted` | Android → Windows | Add a notification |
| `notification.updated` | Android → Windows | Replace a newer revision |
| `notification.removed` | Android → Windows | Remove a notification |
| `notification.action.invoke` | Windows → Android | Request one safe action |
| `notification.action.result` | Android → Windows | Report the action result |

All messages require an authenticated connection and negotiated
`notifications-v1`. The existing connection reader routes them; feature managers
must never read the socket directly.

## Identity, ordering and deduplication

- `notificationId` is a base64url SHA-256 token derived from the Android
  `StatusBarNotification.key`. The raw key is not sent.
- The logical key is `(packageName, notificationId)`.
- `revision` is positive and monotonically increases while the logical notification
  is active.
- Windows ignores a posted/updated/removed message whose revision is older than the
  latest known revision.
- Repeating the same revision and content is idempotent.
- A removal tombstone is retained for the current session so a delayed update cannot
  resurrect a removed notification.

## Posted and updated payload

`notification.posted` contains:

```json
{
  "notificationId": "mOrzZP0Tei8zz...",
  "packageName": "org.example.chat",
  "appName": "Example Chat",
  "title": "Alice",
  "text": "See you at 18:00",
  "postedAtUtc": "2026-07-16T09:30:00Z",
  "category": "msg",
  "groupKey": "chat",
  "isSilent": false,
  "isSensitive": false,
  "iconToken": "app:org.example.chat",
  "revision": 1,
  "actions": []
}
```

`notification.updated` has the same fields plus `updatedAtUtc`.

Limits after UTF-8 sanitization:

- ID/package/icon/action ID: 256 characters;
- app name/title/action title/category/group: 256 characters;
- text: 8 KiB;
- at most three actions;
- the protocol-wide JSON payload limit remains authoritative.

## Removal

```json
{
  "notificationId": "mOrzZP0Tei8zz...",
  "packageName": "org.example.chat",
  "removedAtUtc": "2026-07-16T09:35:00Z",
  "reason": "removed",
  "revision": 2
}
```

## Actions

Android exposes only an opaque `actionId`, a display title and a coarse semantic:
`open`, `dismiss`, `reply` or `custom`. V1 does not transmit `PendingIntent`,
`RemoteViews`, `RemoteInput`, extras or provider data.

Every action defaults to `requiresConfirmation: true`. Windows must show a local
confirmation dialog. Destructive actions and URL launching are not supported in V1.
Android executes an invocation only if it belongs to an active notification, the
action is still registered, `confirmedByUser` is true and the action is not marked
destructive. Results use status `completed`, `rejected`, `expired`, `not_found` or
`failed`.

## Sender policy

- Forwarding is off by default.
- The per-app allowlist is empty by default. A denylist always wins.
- Private/secret notifications are off by default; secret notifications are never
  forwarded.
- Group summaries, foreground-service noise, media/progress notifications and
  excessively frequent equivalent updates are filtered.
- A per-package and global rate limiter prevents notification storms.
- Payload text is normalized, control characters are removed and length is bounded.

## Receiver policy

- History is limited to the current session and can be cleared locally.
- Per-app blocking and quiet hours suppress system notifications without changing
  Android state.
- Sensitive text is hidden while the Windows privacy shield is active.
- A system notification opens DeviceSync; it never automatically opens a URL or
  performs an action.

## Threat and privacy model

Notification access is highly sensitive. TLS protects transport confidentiality, but
both paired endpoints and their unlocked users remain trusted. DeviceSync therefore
minimizes data before transmission, never logs title/text/actions, stores no
notification history on disk, avoids analytics, and requires explicit Android
permission plus explicit per-app selection. Revoking listener access stops new
events; reconnect/rebind does not replay old notifications.

Cross-platform vectors live in `protocol/test-vectors/notifications/`.
