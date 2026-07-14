[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src\DeviceSync.App\DeviceSync.App.csproj'
$appFile = Join-Path $projectRoot 'src\DeviceSync.App\bin\Debug\net8.0-windows\DeviceSync.App.exe'

try {
    Get-Process -Name 'DeviceSync.App' -ErrorAction SilentlyContinue |
        Stop-Process -Force

    dotnet build $projectFile --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "DeviceSync build failed with exit code $LASTEXITCODE."
    }

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
