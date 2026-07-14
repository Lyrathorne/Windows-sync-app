# Manual Android to Windows File Transfer Test

## Prerequisites

- Build and start the latest Windows DeviceSync application.
- Install and start the matching Android debug build.
- Pair the devices and verify that reconnect authentication succeeds.
- Confirm that both hello messages advertise `file-transfer-v1`.

The current transport is authenticated but remains plaintext until the TLS
milestone is completed. Use non-sensitive test files only.

## Happy path

1. Open the connected Windows device on Android.
2. Select **Send file**.
3. Choose a small text file with the system `OpenDocument` picker.
4. Verify the Android screen shows the filename, size, MIME type, and target PC.
5. Press **Send**.
6. Verify Windows shows an incoming-file window and does not accept automatically.
7. Choose a destination folder and press **Accept**.
8. Wait until Android reports that Windows saved the file.
9. Verify Windows shows 100%, `Completed`, and enables **Open file** and
   **Open folder**.
10. Compare SHA-256 of the source and destination files.

On Windows, SHA-256 can be calculated with:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '<destination>'
```

## Test matrix

| Scenario | Expected result |
|---|---|
| Empty file | Completes with zero chunks |
| 1 KiB TXT | One short final chunk |
| 1 MiB JPEG | Multiple chunks and matching SHA-256 |
| Cyrillic filename | Safe filename is preserved |
| Long filename | Safely shortened or rejected |
| Existing filename | New `name (1).ext` destination |
| Reject on Windows | Android shows rejection and sends no chunks |
| Cancel on Android | Windows removes `.part` |
| Cancel on Windows | Android stops reading and shows cancellation |
| Disable Wi-Fi | Both sides fail; Windows removes `.part` |
| Corrupt a chunk in a test transport | Windows reports checksum mismatch |

## Filesystem assertions

- A ready destination file appears only after `file.complete` and hash verification.
- No `.devicesync-*.part` remains after success, reject, cancel, disconnect, or error.
- An existing destination is never overwritten.
- Sender-provided path components never escape the chosen receive directory.

## Result record

Record the Android build SHA, Windows build SHA, device names, file sizes,
source/destination SHA-256 values, and any failure code. Do not record file
contents or sensitive full paths in logs.
