# Folder Sync V1

Folder sync is opt-in and operates only on roots explicitly selected by the user.
Peers first exchange immutable manifests. Planning never writes files; execution starts only after both peers approve the same plan.

- `folder.manifest` contains relative paths, byte sizes, UTC modification times and SHA-256.
- `folder.plan` contains proposed upload/download operations and conflicts.
- `folder.plan.approved` contains the user's explicit resolution for every conflict.
- Paths use `/`, are relative, and may not contain empty, `.` or `..` segments.
- Equal SHA-256 means unchanged regardless of timestamp.
- Different hashes at the same relative path always create a conflict.
- Missing files are copied to the missing side; they are not interpreted as deletions.
- Deletion propagation is disabled by default and is not part of V1 execution.
- Synchronization may be restricted to unmetered Wi-Fi and charging on Android.
- Each file operation is executed through File Transfer V2 after explicit plan approval.
- One Windows root is paired with one Android SAF tree and persisted locally on each device.
- Both devices must approve. Different approval payloads stop execution without writing files.
- Conflict resolutions are `keep_windows`, `keep_android`, and `keep_both`.
- `keep_both` stores the incoming file under a name containing `(from Windows)` or `(from Android)`.
- A folder file offer carries `folderSyncId`, safe `relativePath`, and `conflictCopy`. The receiver accepts it automatically only when the exact path, size and SHA-256 were authorized by the jointly approved plan.
- Replacement is staged under a temporary name. The verified SHA-256 file is committed only after the complete payload is received; failed transfers remove the partial file.
- Folder paths additionally reject drive syntax, colons and NUL characters.

## Messages

```text
folder.manifest
folder.plan
folder.plan.approved
folder.cancel
folder.error
```

The planner is deterministic: sorting both manifests by ordinal relative path must
produce identical plans on Android and Windows.

## Approval example

```json
{
  "syncId": "7d672bba-71c4-4f5f-a635-a2e320cdfa2a",
  "conflictResolutions": [
    { "relativePath": "Documents/report.txt", "resolution": "keep_both" }
  ]
}
```

`upload` and `download` are relative to the device that initiated the sync. The responder verifies the received plan by rebuilding it from the two manifests before presenting approval UI.
