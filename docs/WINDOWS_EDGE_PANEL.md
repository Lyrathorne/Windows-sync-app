# Windows edge panel

The DeviceSync Windows client now has two complementary shells:

- `MainWindow` remains the full legacy/diagnostic window so no existing feature is lost during migration.
- `EdgePanelWindow` is the compact daily-use shell with Devices, Clipboard, Files, Notifications, and Settings sections.

## Behaviour

The panel persists its enabled state, collapsed/expanded state, selected section, left/right side, selected monitor, and hover delay in `%LOCALAPPDATA%\DeviceSync\edge-panel.json`. Invalid JSON falls back to safe defaults instead of preventing startup.

Collapsed width is 18 device-independent pixels and expanded width is 420. Geometry is calculated in physical pixels from each monitor work area and effective DPI, including monitors with negative virtual-desktop coordinates. Width is clamped when a work area is unusually narrow.

The tray menu can:

- open the full window;
- expand or collapse the panel;
- enable or disable it completely;
- select the left/right edge;
- select a connected monitor;
- exit DeviceSync.

Closing the full window continues to hide it to tray. Disabling the edge panel hides only the panel; networking and background services remain active.

## Focus and fullscreen safety

The edge window has `ShowActivated=false`, `ShowInTaskbar=false`, and `Topmost=false`. It is never maintained as an always-on-top window. Hover expansion uses `SetWindowPos` with `SWP_NOACTIVATE`; it may be raised only to the top of the normal, non-topmost band. A lightweight cursor-edge probe allows hover discovery even when the collapsed rail is behind another normal window.

Automatic expansion is refused while another foreground window covers its monitor bounds, which protects games, video, and presentations. Explicit rail/tray activation is still allowed because it is a direct user action.

## Accessibility and input

- Tab and Shift+Tab follow the normal WPF focus order.
- Escape collapses the panel.
- Alt+1 through Alt+5 switch sections.
- Buttons and content regions have automation names.
- Interactive targets are at least 44 pixels high.
- Content scrolls and text wraps rather than depending on a fixed-height screen.

## Verification

`EdgePanelGeometryTests` cover left/right placement, negative monitor coordinates, 125–200% DPI scaling, collapsed width, and narrow-monitor clamping. `EdgePanelStateTests` cover expanded/collapsed state, section selection, persisted settings, value clamping, and corrupt-setting recovery. WPF XAML compilation verifies resource and control templates.

Manual release checks should still cover monitor hot-plug, taskbar on each edge, games using exclusive fullscreen, Windows high contrast, keyboard-only navigation, Narrator, and scale factors 125%, 150%, 175%, and 200%.
