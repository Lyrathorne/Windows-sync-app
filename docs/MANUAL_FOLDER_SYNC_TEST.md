# Manual folder sync test

Prerequisites: Windows and Android are paired, authenticated over pinned TLS, and both advertise `folder-sync-v1` and `file-transfer-v2`.

1. Create matching test roots on Windows and Android.
2. Add `windows-only.txt` only on Windows and `android-only.txt` only on Android.
3. Add different contents under the same `conflict.txt` path on both devices.
4. On one device select its root and start folder sync. On the peer, select the matching root when requested.
5. Verify both devices show one upload, one download, and one conflict. Verify no file changed yet.
6. Select the same resolution for `conflict.txt` on both devices and approve both plans.
7. Wait for the sequential file-transfer queue to finish. Verify SHA-256 for both missing files.
8. Repeat for each conflict resolution:
   - `keep_windows`: both roots contain the Windows bytes under `conflict.txt`.
   - `keep_android`: both roots contain the Android bytes under `conflict.txt`.
   - `keep_both`: the original local file remains and a verified `(from Windows)` or `(from Android)` copy appears.
9. During a large transfer disable Wi-Fi. Verify no unverified final file appears and partial data is cleaned or resumable according to the receiver mode.
10. Send mismatched approvals. Verify execution stops and neither root changes.
11. Try relative paths containing `..`, an absolute path, a drive prefix, a colon, and NUL through a protocol test client. Verify every offer is rejected.

Folder sync never propagates deletion in V1. Files larger than the File Transfer limit of 100 MiB are rejected.
