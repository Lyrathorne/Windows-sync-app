# DeviceSync design system

## Principles

DeviceSync should feel calm, trustworthy, and fast. Its visual language is original: a deep evergreen primary color, cool neutral surfaces, rounded but compact cards, and restrained motion. Do not copy the branding, icons, layout, or animation language of Windows Link, Yandex Keyboard, Gboard, or other products.

The system is semantic. Feature UI must reference roles such as `surface`, `onSurface`, `attention`, or `connected`, never a raw color chosen for one screen.

## Color roles

| Role | Light | Dark | High contrast | Use |
|---|---:|---:|---:|---|
| background | `#F6FAF8` | `#101513` | `#000000` | Window/screen background |
| surface | `#FFFFFF` | `#18201D` | `#000000` | Cards and panels |
| surfaceVariant | `#E8F0ED` | `#25302C` | `#1A1A1A` | Secondary containers |
| onSurface | `#15201D` | `#E4ECE8` | `#FFFFFF` | Main text |
| onSurfaceMuted | `#4B625B` | `#B7C9C3` | `#FFFFFF` | Supporting text |
| outline | `#70817C` | `#899B95` | `#FFFFFF` | Borders and dividers |
| primary | `#006B5B` | `#8BD8C4` | `#00FFFF` | Primary actions and focus |
| onPrimary | `#FFFFFF` | `#00382F` | `#000000` | Content over primary |
| connected | `#18794E` | `#66D69E` | `#00FF66` | Connected/success |
| syncing | `#9A6500` | `#F4C95D` | `#FFFF00` | Work in progress |
| offline | `#5E6C68` | `#AAB8B3` | `#FFFFFF` | Inactive/offline |
| error | `#BA1A1A` | `#FFB4AB` | `#FF4D4D` | Failure/destructive |
| attention | `#8A4F00` | `#FFB86B` | `#FFFF00` | User action required |

Normal text and controls target WCAG 2.2 AA: at least 4.5:1 for body text and 3:1 for large text, focus indicators, and non-text controls. Status is never communicated by color alone; pair it with a label and, where useful, an icon.

## Typography

Use the platform UI font (`Segoe UI Variable`/`Segoe UI` on Windows and the Android system sans family). Respect OS font scaling; never convert user-scaled text into fixed bitmap assets.

- Display: 32/40, semibold — rare product headings.
- Title large: 24/32, semibold.
- Title: 20/28, semibold.
- Body: 16/24, regular.
- Body small: 14/20, regular.
- Label: 13/18, semibold.
- Monospace is reserved for diagnostic IDs and technical codes.

Text must wrap or ellipsize deliberately. Cards cannot use a fixed height for user content. Verify Android at font scale 1.0, 1.3, and 2.0 and Windows at 125%, 150%, and 200% display scale.

## Layout tokens

Spacing follows a 4-unit grid: `4, 8, 12, 16, 24, 32, 48`. Default screen padding is 24 on desktop and 16 dp on Android. Minimum interactive target is 44 px on Windows and 48 dp on Android.

Corner radii: 8 compact, 12 controls, 16 cards, 24 prominent containers/pills. Elevation is subtle: level 0 flat, level 1 cards, level 2 floating panels, level 3 modal content. Prefer borders in high contrast mode because shadows may be invisible.

## Icons

Use the native platform icon vocabulary where possible (Fluent-style geometry on Windows and Material Symbols-compatible geometry on Android), while keeping DeviceSync-specific symbols original. Standard inline icons are 20–24 units; prominent empty-state icons may be 40–48. Keep a consistent 2-unit optical stroke, provide an accessible label for icon-only actions, and do not use emoji as functional icons. Destructive, warning, and success meaning must include text or an accessibility description. Product branding uses a separate owned DeviceSync mark and never reuses a third-party logo.

## Component states

- Hover: slightly stronger container/outline; never move layout.
- Pressed: immediate darker/lighter state with 60–100 ms visual response.
- Focus: 2 px primary focus ring with sufficient contrast and keyboard visibility.
- Disabled: reduced emphasis but readable; disabled state is also exposed to accessibility APIs.
- Loading: keep the original control width, disable duplicate actions, and show concise progress text.
- Error: stable technical code may be shown in details, while the main message explains recovery.

Motion duration is normally 100–200 ms. Respect reduced-animation preferences. Indeterminate animation is used only when progress truly cannot be calculated.

## Status pattern

`Connected`, `Syncing`, `Offline`, `Error`, and `Attention` consist of a colored 8–10 px dot, a text label, and an accessible description. `Syncing` may animate, but its text remains present. `Attention` means a user decision or permission is required, not a generic warning.

## Cards

- Device card: device name, connection status, last activity, and one primary action.
- File card: safe display name, size/type, transfer state, byte progress, and cancel/retry action.
- Clipboard card: two-to-four-line preview, origin/time, sensitivity/manual indicators, and copy/send actions.
- Notification card: source app, timestamp, title, two-to-three-line body, and safe actions.

Cards use `surface`, a one-pixel outline, 16 radius, and 16 internal padding. Lists use 8–12 spacing. Long content is never forced into a single clipped line.

## Feedback patterns

- Skeleton: surface-variant blocks shaped like final content; no fake text; announce loading once rather than every block.
- Determinate progress: percentage plus bytes/items when meaningful.
- Empty state: concise reason, one next action, no decorative obstruction.
- Error state: what failed, what data is safe, and a retry/settings action.

## Platform implementation

Windows tokens live in `windows/src/DeviceSync.App/Themes`. `DesignTokens.Light.xaml` supplies the complete base token set. To switch modes, merge the dark or high-contrast color override after the base dictionary; dynamic brushes and component styles then update without screen-specific changes. `ComponentStyles.xaml` contains dynamic-resource styles. `DesignSystemCatalogWindow` is a development catalog and is not inserted into production navigation.

Android tokens live in `ui/theme`, while reusable components and the preview catalog live in `ui/designsystem`. `DeviceSyncTheme` accepts `highContrast` independently of light/dark choice. The catalog is previewable and is not part of the navigation graph.

Before migrating a production screen, replace raw colors and dimensions with tokens, verify keyboard/focus order, screen-reader labels, high contrast, long translated strings, and scaled text. P05 establishes the system; P06–P08 apply it to the actual shells and screens.
