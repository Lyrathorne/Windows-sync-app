# DeviceSync real-device test matrix

This matrix is release evidence, not a claim that every row has already passed. Automated tests do
not replace OEM background, vibration, Bluetooth, accessibility or physical performance checks.
BlueStacks is not accepted as evidence for those areas. The camera must not be used.

Status values: `NOT RUN`, `PASS`, `FAIL`, `BLOCKED`.

| Area | Device / environment | Required scenarios | Status | Evidence / notes |
|---|---|---|---|---|
| Android 8 | API 26 physical device | pairing, TLS reconnect, foreground service, clipboard, 1 MiB file | NOT RUN | |
| Android 11 | API 30 physical device | scoped storage, notification listener, reboot recovery | NOT RUN | |
| Android 13 | API 33 physical device | notification/media permissions, background reconnect | NOT RUN | |
| Android 14 | API 34 physical device | foreground-service restrictions, selected media access | NOT RUN | |
| Android 15+ | API 35/36 physical device | current target SDK behavior, IME, Doze | NOT RUN | |
| Infinix / XOS | Infinix X6871 or current target phone | app open/background/screen off/Doze/reboot/force stop | NOT RUN | User-reported priority |
| Second OEM | Samsung, Xiaomi, Pixel or equivalent | OEM battery restrictions and autostart instructions | NOT RUN | |
| Navigation | Gesture navigation | IME and panels stay above system gesture area | NOT RUN | |
| Navigation | Three-button navigation | IME and panels stay above system buttons | NOT RUN | |
| Keyboard | Physical low/mid-range phones | haptics, fast typing, T9 p50/p95/p99, emoji, large font | NOT RUN | No camera |
| LAN | Same Wi-Fi | auth, reconnect, clipboard, notifications, catalog, files | NOT RUN | |
| Hotspot | Android or Windows hotspot | discovery fallback and remembered endpoint | NOT RUN | |
| USB | USB tethering | preferred transport, reconnect after cable cycle | NOT RUN | |
| Bluetooth | RFCOMM fallback | TLS pinning, 2 MiB limit, disabled catalog capabilities | NOT RUN | Physical devices only |
| Network switch | Wi-Fi ↔ hotspot ↔ USB | old session closes once; fresh authenticated connection | NOT RUN | |
| Battery | saver / Doze / screen off | expected foreground notification; reconnect survives permitted states | NOT RUN | |
| Process | reboot / process kill / force stop | reboot recovery works; force stop is documented as OS-controlled | NOT RUN | |
| Windows display | 100/125/150/200% DPI | main window and edge panel remain usable | NOT RUN | |
| Windows display | multi-monitor | selected edge/monitor, unplug/replug, taskbar placement | NOT RUN | |
| Windows lock | locked/unlocked | sensitive notifications hidden and sessions recover | NOT RUN | |
| Storage | low disk | transfer rejected, `.part` removed, no corrupt final file | NOT RUN | |
| Large data | 50k media items / 100 MiB file | pagination, bounded thumbnails, streaming transfer | NOT RUN | |
| Accessibility | TalkBack / Narrator / keyboard-only | labels, focus order, contrast, touch targets | NOT RUN | |

## Evidence rules

- Record application versions, OS builds, transport and outcome.
- Attach screenshots only when useful and without exposing private content; do not use the camera.
- Hash test files rather than attaching their contents.
- Export the privacy-safe support bundle after failures.
- A row remains `NOT RUN` until tested on the specified physical environment.
