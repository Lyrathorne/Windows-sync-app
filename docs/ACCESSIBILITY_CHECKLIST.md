# DeviceSync accessibility and responsive checklist

This checklist covers the Android application, DeviceSync Keyboard, the Windows edge panel, pairing, file acceptance and the full Windows diagnostics window.

## Automated safeguards

- [x] Android default (RU) and `values-en` resource keys are checked for parity.
- [x] Android Home has a Compose UI test at 360 dp width and font scale 2.0.
- [x] Android primary actions use Material controls with a minimum 48 dp touch target.
- [x] Windows RU/EN resource dictionary keys are checked for parity.
- [x] Windows edge-panel geometry is tested at 100%, 125%, 150%, 200% clamping and 250% DPI.
- [x] Windows respects the system high-contrast setting and updates while the application is running.
- [x] Windows edge-panel movement is disabled when system client-area animations are disabled.

## Manual Android matrix

Run each row in both Russian and English. Verify that no action is hidden, clipped or covered by system bars.

| Device/configuration | Portrait | Landscape | TalkBack | Result |
|---|---:|---:|---:|---|
| 320–360 dp phone, font 0.85 | [ ] | [ ] | [ ] | Pending |
| 360–420 dp phone, font 1.0 | [ ] | [ ] | [ ] | Pending |
| 360–420 dp phone, font 1.3 | [ ] | [ ] | [ ] | Pending |
| 360–420 dp phone, font 2.0 | [ ] | [ ] | [ ] | Pending |
| Foldable/tablet 600–840 dp | [ ] | [ ] | [ ] | Pending |
| Three-button navigation | [ ] | [ ] | [ ] | Pending |
| Gesture navigation | [ ] | [ ] | [ ] | Pending |

Check these flows:

- [ ] Home announces the heading, computer, connection status and actionable background warning in a sensible order.
- [ ] Bottom navigation announces localized section names and selected state.
- [ ] Quick actions remain fully visible at font scale 2.0 and become a single column when needed.
- [ ] Files empty/history states and long Cyrillic file names do not overlap.
- [ ] Notification settings remain scrollable with long translations.
- [ ] Settings switches announce their label, state and action together.
- [ ] Pairing, incoming file, outgoing file and error dialogs retain focus after configuration changes.
- [ ] Keyboard keys, toolbar, suggestions, emoji and clipboard panels have no targets smaller than 48 dp unless IME platform conventions require compact keys.
- [ ] No nonessential animation runs when Android “Remove animations” is enabled.

## Manual Windows matrix

Run with keyboard only and Narrator. Use both RU and EN Windows display language where available.

| DPI/configuration | Edge panel | Full window | Pairing | Incoming file | Result |
|---|---:|---:|---:|---:|---|
| 100% | [ ] | [ ] | [ ] | [ ] | Pending |
| 125% | [ ] | [ ] | [ ] | [ ] | Pending |
| 150% | [ ] | [ ] | [ ] | [ ] | Pending |
| 200% | [ ] | [ ] | [ ] | [ ] | Pending |
| 250% | [ ] | [ ] | [ ] | [ ] | Pending |
| High contrast | [ ] | [ ] | [ ] | [ ] | Pending |
| Animations disabled | [ ] | N/A | N/A | N/A | Pending |

Check these behaviours:

- [ ] Tab and Shift+Tab move through controls in visual order without trapping focus.
- [ ] Enter/Space opens the collapsed edge rail; Escape collapses it.
- [ ] Alt+1…5 switches edge-panel sections.
- [ ] Focus indicators remain visible in light and high-contrast themes.
- [ ] Narrator announces navigation, file-transfer progress, pairing state and actionable errors.
- [ ] Buttons wrap instead of overlapping at 250% DPI.
- [ ] Long Russian labels wrap without losing actions.
- [ ] The edge panel stays inside the selected monitor work area, including negative coordinates.
- [ ] Opening the panel does not steal focus from a foreground full-screen application.

## Release gate

Do not close an accessibility regression if it prevents pairing, accepting/rejecting a file, cancelling a transfer, navigating back, or disabling background/clipboard features. Record the device, OS build, locale, font/DPI scale and assistive technology with every issue.
