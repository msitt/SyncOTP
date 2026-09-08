<#
.SYNOPSIS
    Builds SyncOTP and installs it to %LOCALAPPDATA%\Programs\SyncOTP.

.DESCRIPTION
    This is both the installer and the update path. Config (%APPDATA%\SyncOTP) and logs
    (%LOCALAPPDATA%\SyncOTP) are never touched, so re-running it keeps your settings.

.PARAMETER Startup
    Also register SyncOTP to start with Windows (HKCU Run key).

.PARAMETER NoLaunch
    Install but do not start the app afterwards.

.EXAMPLE
    .\scripts\install.ps1 -Startup
#>
[CmdletBinding()]
param(
    [switch] $Startup,
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\SyncOTP.App\SyncOTP.App.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\SyncOTP'
$exePath    = Join-Path $installDir 'SyncOTP.exe'
$publishDir = Join-Path $repoRoot 'artifacts\publish'

Write-Host 'Building SyncOTP...' -ForegroundColor Cyan
dotnet publish $project -c Release -o $publishDir --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# The running instance holds a lock on its own exe, so it has to go first.
$running = Get-Process -Name 'SyncOTP' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping the running instance...' -ForegroundColor Cyan
    $running | Stop-Process -Force
    # Give the tray icon time to disappear before the file is replaced.
    Start-Sleep -Milliseconds 700
}

if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Write-Host "Installing to $installDir" -ForegroundColor Cyan
Copy-Item -Path (Join-Path $publishDir '*') -Destination $installDir -Recurse -Force

if ($Startup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $runKey -Name 'SyncOTP' -Value "`"$exePath`""
    Write-Host 'Registered to start with Windows.' -ForegroundColor Green
}

if (-not $NoLaunch) {
    Write-Host 'Starting SyncOTP...' -ForegroundColor Cyan
    Start-Process -FilePath $exePath
}

Write-Host ''
Write-Host "Installed:  $exePath"          -ForegroundColor Green
Write-Host "Config:     $env:APPDATA\SyncOTP\config.json"
Write-Host "Logs:       $env:LOCALAPPDATA\SyncOTP\logs"
Write-Host ''
Write-Host 'Next: set ntfy.server and ntfy.topic (and credentials, if your server needs them) in config.json, then use the tray menu to reload.'
