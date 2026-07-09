# DeviceSync Windows

The Windows app is a WPF/.NET 8 application that hosts the TCP side of DeviceSync Protocol V1.

## Discovery

The app publishes `_devicesync._tcp` with a small built-in mDNS/DNS-SD publisher isolated behind `IServiceDiscoveryPublisher`.

No third-party mDNS package is used at this stage. The implementation is intentionally isolated in `DeviceSync.Infrastructure.SimpleMdnsServiceDiscoveryPublisher` so it can be replaced by a Windows DNS-SD API or a maintained library later without changing ViewModels or application logic.

TXT records:

`deviceId`, `deviceName`, `deviceType`, `protocolMin`, `protocolMax`, `appVersion`, `pairingAvailable`, `capabilities`.

mDNS/DNS-SD is only for address discovery. It does not authenticate the computer; TCP hello handshake is still required, and cryptographic pairing is left for a later stage.

## Windows Firewall

If Android can see the Windows computer but TCP connection times out, first check whether Windows is listening:

```powershell
Get-NetTCPConnection -LocalPort 54321
```

Manual firewall rule for diagnostics:

```powershell
New-NetFirewallRule `
  -DisplayName "DeviceSync TCP 54321" `
  -Direction Inbound `
  -Protocol TCP `
  -LocalPort 54321 `
  -Action Allow `
  -Profile Any
```
