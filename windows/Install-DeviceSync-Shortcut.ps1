[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$desktop = [Environment]::GetFolderPath('Desktop')
$launcher = Join-Path $PSScriptRoot 'Run-DeviceSync-Latest.ps1'
$appFile = Join-Path $PSScriptRoot 'src\DeviceSync.App\bin\Debug\net8.0-windows\DeviceSync.App.exe'
$shortcutPath = Join-Path $desktop 'DeviceSync (latest).lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = (Get-Command powershell.exe).Source
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$launcher`""
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.IconLocation = "$appFile,0"
$shortcut.Description = 'Build and run the latest DeviceSync Windows version'
$shortcut.Save()

Write-Host "Shortcut created: $shortcutPath"
