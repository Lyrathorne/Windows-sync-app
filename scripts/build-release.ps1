param(
    [string]$Configuration = "Release",
    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "release-current"
$legacyPublish = Join-Path $root "windows\DeviceSync-current"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "DeviceSyncReleaseBuild"
$buildArtifacts = Join-Path $temporaryRoot "artifacts"
$staging = Join-Path $temporaryRoot "publish"

if (Test-Path -LiteralPath $temporaryRoot) {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

$env:DEVICESYNC_APP_SOURCE = Join-Path $root "windows\src\DeviceSync.App"
dotnet test (Join-Path $root "windows\DeviceSync.sln") -c $Configuration `
    "-p:ArtifactsPath=$buildArtifacts"
dotnet publish (Join-Path $root "windows\src\DeviceSync.App\DeviceSync.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    "-p:ArtifactsPath=$buildArtifacts" -o $staging

if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
Move-Item -LiteralPath $staging -Destination $publish
if (Test-Path -LiteralPath $legacyPublish) {
    Remove-Item -LiteralPath $legacyPublish -Recurse -Force
}
Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue

if ($BuildInstaller) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) is required to build the installer." }
    & $iscc.Source (Join-Path $root "windows\installer\DeviceSync.iss")
}

Get-FileHash (Join-Path $publish "DeviceSync.App.exe") -Algorithm SHA256
