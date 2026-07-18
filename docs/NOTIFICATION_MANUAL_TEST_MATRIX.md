# Notification forwarding manual test matrix

Run with paired devices over the authenticated TLS connection. Enable Notification
Listener access on Android and explicitly allow only the test applications.

| Scenario | Expected |
|---|---|
| Messenger receives a normal message | One Windows item and one system notification |
| Messenger edits the same notification | Existing item updates; no duplicate |
| Messenger notification is dismissed | Windows item disappears |
| Incoming call | Visible unless the app is blocked or quiet hours apply |
| Ongoing foreground-service notification | Not forwarded |
| Media playback/progress update | Not forwarded |
| Group summary plus child messages | Only useful child messages appear |
| Private lock-screen notification, default settings | Not forwarded |
| Private notification after explicit opt-in | Forwarded and marked sensitive |
| Secret notification | Never forwarded |
| More than 20 updates/minute from one app | Excess updates are rate-limited |
| Listener permission revoked | New events stop; UI reports access is missing |
| Listener permission restored | New events resume without a second socket reader |
| Android disconnect/reconnect | No old notification replay; new events work |
| Windows quiet hours | History updates, system notification is suppressed |
| Windows privacy shield active | Sensitive body text is not shown |
| Safe action with local confirmation | Android executes it and returns a result |
| Action without confirmation/destructive action | Rejected |
