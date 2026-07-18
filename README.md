# DeviceSync for Windows

[English](README.md) | [Русский](README.ru.md)

DeviceSync connects a Windows PC and an Android phone directly over a local network. It provides authenticated pairing, resumable file transfer, clipboard sharing, phone notifications, and access to permitted phone files without routing everyday traffic through a cloud service.

<p align="center">
  <img src="docs/images/windows-overview.png" width="360" alt="DeviceSync Windows edge panel showing file transfer controls">
</p>

> DeviceSync is under active development. Review the [current limitations](#current-limitations) before relying on it for important data.

## Contents

- [What it does](#what-it-does)
- [Screenshots](#screenshots)
- [Requirements](#requirements)
- [Install and run](#install-and-run)
- [Build from source](#build-from-source)
- [Pair a phone](#pair-a-phone)
- [Everyday use](#everyday-use)
- [Architecture](#architecture)
- [Security and privacy](#security-and-privacy)
- [Development and testing](#development-and-testing)
- [Troubleshooting](#troubleshooting)
- [Current limitations](#current-limitations)
- [Contributing and licensing](#contributing-and-licensing)

## What it does

The Windows application acts as the desktop endpoint of DeviceSync. It listens for an authenticated Android client, advertises itself on the local network, and exposes each negotiated feature through a WPF interface and an optional screen-edge panel.

Implemented capabilities include:

- automatic discovery with mDNS/DNS-SD and a UDP beacon, plus manual IP connection from Android;
- QR pairing with a short verification flow and persistent trusted-device identities;
- TLS 1.2/1.3 transport with Android-side SPKI pin verification and a signed application handshake;
- bidirectional, streamed file transfers with SHA-256 verification and resumable V2 transfers;
- manual and opt-in automatic text clipboard sharing with loop prevention;
- Android notification forwarding with per-app filtering and privacy controls;
- browsing permitted Android media and folders, thumbnails, and downloads to Windows;
- experimental folder synchronization;
- automatic reconnect across LAN, hotspot, USB tethering, and a slower Bluetooth RFCOMM fallback;
- English and Russian UI resources, tray operation, autostart, diagnostics, and the edge panel.

Feature availability is negotiated per connection. A control may be unavailable when the Android build does not advertise the matching capability.

## Screenshots

<p align="center">
  <img src="docs/images/windows-devices.png" width="30%" alt="Windows device list and phone connection action">
  <img src="docs/images/windows-pairing.png" width="30%" alt="Windows QR code used to pair an Android phone">
  <img src="docs/images/windows-clipboard.png" width="30%" alt="Windows clipboard synchronization controls">
</p>

<p align="center">
  <img src="docs/images/windows-files.png" width="30%" alt="Windows file transfer and phone files controls">
  <img src="docs/images/windows-notifications.png" width="30%" alt="Android notification controls on Windows">
  <img src="docs/images/windows-settings.png" width="30%" alt="Windows edge panel and autostart settings">
</p>

## Requirements

| Use case | Requirement |
|---|---|
| Run a published build | 64-bit Windows 10 or Windows 11; a compatible DeviceSync Android app |
| Connect over LAN | Both devices on the same reachable private network; TCP port `54321` allowed for DeviceSync |
| Build from source | .NET 8 SDK and PowerShell |
| Build the installer | Inno Setup 6 with `ISCC.exe` available on `PATH` |
| Bluetooth fallback | Bluetooth available and the PC and phone paired in Windows and Android settings |

The published Windows build is self-contained, so it does not require a separately installed .NET Desktop Runtime. Source builds require the .NET 8 SDK.

## Install and run

Prebuilt EXE and ZIP outputs are intentionally excluded from Git. Obtain a trusted packaged release from the repository owner when one is published, or build it locally as described below.

For an existing local publish:

```powershell
.\release-current\DeviceSync.App.exe
```

The repository also provides a launcher that rebuilds the latest Release configuration and starts it:

```powershell
.\windows\Run-DeviceSync-Latest.ps1
```

> An unsigned local build may trigger Microsoft Defender SmartScreen. Verify where the binary came from; do not bypass a warning for an untrusted download.

## Build from source

Run these commands from the repository root:

```powershell
dotnet restore .\windows\DeviceSync.sln
dotnet build .\windows\DeviceSync.sln -c Debug
dotnet run --project .\windows\src\DeviceSync.App\DeviceSync.App.csproj
```

Run all Windows tests:

```powershell
dotnet test .\windows\DeviceSync.sln -c Debug
```

Create the tested self-contained `win-x64` build:

```powershell
.\scripts\build-release.ps1
```

The resulting executable is:

```text
release-current\DeviceSync.App.exe
```

To build the installer as well:

```powershell
.\scripts\build-release.ps1 -BuildInstaller
```

| Output | Purpose |
|---|---|
| Debug | Fast local development and debugging; framework-oriented build output under project `bin` directories |
| Release | Optimized compilation used by tests or manual runs |
| Published build | Self-contained, single-file, ReadyToRun `win-x64` application in `release-current` |

`bin`, `obj`, `windows/artifacts`, `windows/DeviceSync-current`, and `release-current` are generated outputs and must not be committed.

## Pair a phone

1. Install compatible DeviceSync builds on Windows and Android.
2. Connect both devices to the same private LAN. Start the Windows application first.
3. Open **Devices** on Windows and select **Connect phone** to generate a time-limited QR code.
4. On Android, choose **Add computer**, allow camera access, and scan the QR code.
5. Verify the pairing information shown by both applications and approve the request.
6. Keep the Android background connection enabled if you want reconnect, notification, or queued-transfer behavior after closing the Android UI.

The Windows endpoint advertises `_devicesync._tcp` and normally listens on TCP `54321`. Discovery only finds an address; QR data, TLS pinning, and the signed identity handshake establish trust.

## Everyday use

### Send a file

Open **Files**, select **Send file**, and choose a connected phone. The receiver must approve the offer unless an explicit trusted-device automation policy permits otherwise. Transfers are streamed, verified with SHA-256, and can use checkpoints when both sides negotiate V2.

### Share clipboard text

Open **Clipboard** and use **Send clipboard now** for a manual transfer. Automatic clipboard synchronization is opt-in and can be controlled per trusted phone. Empty or privacy-sensitive values are not intended to be forwarded.

### View phone files

Open **Files** and select **Open phone files**. Android must grant the relevant media, all-files, or user-selected-folder access before Windows can query that content. Catalog pages and thumbnails are bounded and transferred over the authenticated session.

### Receive Android notifications

Enable notification access in the Android app, then configure allowed applications. Windows can show recent notifications and system toasts, hide sensitive content, and block individual packages.

### Edge panel and autostart

Use **Settings** to enable the edge panel, choose its screen side and hover delay, and configure startup with Windows. `Esc` hides the panel, `Ctrl+Alt+D` opens it, and `Alt+1` through `Alt+5` switch sections.

## Architecture

```text
windows/
├── src/DeviceSync.Protocol        Framing, payloads, capabilities, negotiation
├── src/DeviceSync.Application     Pairing, trust, files, clipboard, notifications
├── src/DeviceSync.Infrastructure  TCP/TLS, Bluetooth, discovery, local persistence
├── src/DeviceSync.App             WPF UI, dependency injection, tray and edge panel
├── tests/                          Unit and loopback integration tests
└── installer/                      Inno Setup definition
protocol/                           Cross-platform protocol specifications and vectors
scripts/                            Release automation
```

`TcpDeviceServer`/`ClientSession` owns each normal session's input loop and routes validated messages to feature managers. The Android app follows the same single-reader principle. Frames use a four-byte big-endian length prefix followed by UTF-8 JSON. Protocol version and capabilities are negotiated before features are enabled.

## Security and privacy

- The normal LAN transport uses TLS 1.2 or 1.3.
- The pairing QR carries the Windows TLS server SPKI fingerprint; Android pins it before connecting.
- Long-lived ECDSA device identities and signed challenge/response messages authenticate reconnects.
- Windows protects private key material with the current user's Windows data protection facilities and stores trust/settings under Local AppData.
- Files are written to temporary storage, size and offsets are validated, and SHA-256 is checked before finalization.
- Notification forwarding, automatic clipboard, file automation, and phone catalog access require explicit user choices.
- Diagnostics are designed to redact clipboard text, notification contents, paths, secrets, and identifiers.

DeviceSync still handles sensitive content. Pair only devices you control, review per-device permissions, and use trusted private networks.

### Windows access used by the app

| Access | Why it is needed |
|---|---|
| Private-network inbound TCP | Accept the authenticated Android connection on port `54321` |
| Local AppData | Store settings, trusted identities, queues, checkpoints, and protected key material |
| Selected files and folders | Send files, receive approved transfers, and perform configured folder sync |
| Clipboard | Manual or opt-in automatic text sharing |
| Notification area and optional autostart | Background/tray operation and reconnect behavior |
| Bluetooth, when selected | Slow RFCOMM fallback after system-level pairing |

## Development and testing

The solution contains protocol, application, UI, and loopback integration test projects. A focused release check is:

```powershell
dotnet test .\windows\DeviceSync.sln -c Release
.\scripts\build-release.ps1
```

Protocol changes should update the specifications and shared JSON vectors in `protocol/test-vectors` as well as tests on both platforms. See [the release checklist](docs/RELEASE_CHECKLIST.md) and [architecture documentation](docs/TRANSPORT_ARCHITECTURE.md).

## Troubleshooting

### Android can see the PC but cannot connect

Confirm the listener:

```powershell
Get-NetTCPConnection -LocalPort 54321
```

Allow DeviceSync on private networks in Windows Firewall. For temporary diagnostics, an administrator can add a scoped rule:

```powershell
New-NetFirewallRule -DisplayName "DeviceSync TCP 54321" -Direction Inbound `
  -Protocol TCP -LocalPort 54321 -Action Allow -Profile Private
```

Do not disable Windows Firewall entirely.

### Discovery does not find the computer

Make sure both devices are on the same non-isolated network and VPNs are not forcing traffic through another interface. Use Android's manual IP option if multicast discovery is blocked.

### Pairing or reconnect fails

Remove the stale trusted-device entry on both platforms and scan a newly generated QR code. A changed Windows identity or TLS pin requires new pairing.

### Background features stop

Confirm that Android background connection is enabled, notifications are allowed, and battery optimization is relaxed if the device vendor suspends DeviceSync.

## Current limitations

- The published Windows target is `win-x64`; ARM64 and x86 packages are not configured.
- The repository does not contain a signed public installer or committed release binary.
- The primary transport expects direct reachability; guest Wi-Fi isolation, corporate firewall policy, and some VPNs can block it.
- Bluetooth is a slow fallback intended for clipboard, commands, and small files, not bulk synchronization.
- Folder sync and the DeviceSync Android keyboard are experimental areas and require additional real-device validation.
- Some release and roadmap documents may lag behind source code; capabilities are the runtime source of truth.

See the [project roadmap](docs/DEVICESYNC_ROADMAP.md) for planned hardening and product work.

## Contributing and licensing

Before submitting a change:

1. keep generated outputs out of Git;
2. add or update tests;
3. update protocol documents and vectors when wire behavior changes;
4. run the Release test suite;
5. describe any security, permission, or migration impact.

No root `LICENSE` file is currently present, so the repository does not state a general reuse license. Ask the repository owner before redistributing or reusing the project. Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

The companion Android repository is [Lyrathorne/Android-sync-app](https://github.com/Lyrathorne/Android-sync-app).
