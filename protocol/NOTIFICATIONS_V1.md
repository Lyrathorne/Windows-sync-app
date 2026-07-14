# Notification Forwarding V1

Capability: `notifications-v1`.

Android is the sender and Windows is the receiver. Messages are
`notification.posted` and `notification.removed`.

- Notification access requires the Android Notification Listener permission.
- Forwarding is disabled by default.
- The application allowlist is empty by default and must be explicitly populated.
- Notifications with `VISIBILITY_SECRET` are never forwarded.
- Only package/app name, title, text, stable notification ID and timestamp are sent.
- Title is limited to 4096 characters and text to 8192 characters.
- Windows replaces an existing history item with the same notification ID and removes
  it on `notification.removed`.
- Notification actions and reply tokens are not forwarded in V1.
- Payloads are protected by the pinned TLS transport and are never written to logs.
