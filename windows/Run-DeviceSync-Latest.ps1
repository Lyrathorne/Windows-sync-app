[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot 'src\DeviceSync.App\DeviceSync.App.csproj'
$publishDirectory = Join-Path $workspaceRoot 'release-current'
$legacyPublishDirectory = Join-Path $projectRoot 'DeviceSync-current'
$appFile = Join-Path $publishDirectory 'DeviceSync.App.exe'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'DeviceSyncLatestBuild'
$buildArtifacts = Join-Path $temporaryRoot 'artifacts'
$stagingDirectory = Join-Path $temporaryRoot 'publish'

try {
    Get-Process -Name 'DeviceSync.App' -ErrorAction SilentlyContinue |
        Stop-Process -Force

    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }

    dotnet publish $projectFile --nologo -c Release -r win-x64 --self-contained true `
        "-p:ArtifactsPath=$buildArtifacts" -o $stagingDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "DeviceSync publish failed with exit code $LASTEXITCODE."
    }

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    Move-Item -LiteralPath $stagingDirectory -Destination $publishDirectory
    if (Test-Path -LiteralPath $legacyPublishDirectory) {
        Remove-Item -LiteralPath $legacyPublishDirectory -Recurse -Force
    }
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path -LiteralPath $appFile)) {
        throw "The built application was not found: $appFile"
    }

    Start-Process -FilePath $appFile -WorkingDirectory (Split-Path $appFile)
}
catch {
    Write-Host ''
    Write-Host "DeviceSync could not be started: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
