#define MyAppName "DeviceSync"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DeviceSync"
#define MyAppExeName "DeviceSync.App.exe"

[Setup]
AppId={{0A4F34C0-0DB2-4C04-9F21-381716644450}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\DeviceSync
DefaultGroupName=DeviceSync
OutputDir=output
OutputBaseFilename=DeviceSync-Setup-{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DeviceSync"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\DeviceSync"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "autostart"; Description: "Start DeviceSync when I sign in"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeviceSync"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch DeviceSync"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; Trusted identities and settings live in the user's LocalAppData and are intentionally preserved
; across upgrade/uninstall. External Authenticode signing is performed only by the release owner.
