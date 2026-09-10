<#
.SYNOPSIS
    Builds SyncOTP and installs it to %LOCALAPPDATA%\Programs\SyncOTP.

.DESCRIPTION
    This is both the installer and the build-from-source update path. Config (%APPDATA%\SyncOTP)
    and logs (%LOCALAPPDATA%\SyncOTP) are never touched, so re-running it keeps your settings.

    The stop/copy/relaunch half is scripts/apply-payload.ps1, shared with the in-app updater, so
    that sequence gets exercised every time anyone installs from source.

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
$applier    = Join-Path $PSScriptRoot 'apply-payload.ps1'

Write-Host 'Building SyncOTP...' -ForegroundColor Cyan
dotnet publish $project -c Release -o $publishDir --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# The app reads this to decide which release zip an update should download. A local build is
# framework-dependent, matching SelfContained=false in SyncOTP.App.csproj.
$version = (Select-Xml -Path (Join-Path $repoRoot 'Directory.Build.props') -XPath '//Version').Node.InnerText
[ordered]@{
    flavor      = 'framework-dependent'
    version     = $version
    installedBy = 'local-build'
} | ConvertTo-Json | Out-File -FilePath (Join-Path $publishDir 'release.json') -Encoding utf8

Write-Host "Installing to $installDir" -ForegroundColor Cyan

# -ForceStop because the user explicitly asked for an install: if the app will not exit on request,
# taking it down anyway is the expected outcome here. The updater deliberately does not do this.
& $applier `
    -Source $publishDir `
    -Target $installDir `
    -Exe $exePath `
    -Backup (Join-Path $env:LOCALAPPDATA 'SyncOTP\updates\backup') `
    -ForceStop `
    -NoLaunch:$NoLaunch

if ($LASTEXITCODE -ne 0) { throw "apply-payload failed with exit code $LASTEXITCODE" }

if ($Startup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $runKey -Name 'SyncOTP' -Value "`"$exePath`""
    Write-Host 'Registered to start with Windows.' -ForegroundColor Green
}

Write-Host ''
Write-Host "Installed:  $exePath"          -ForegroundColor Green
Write-Host "Config:     $env:APPDATA\SyncOTP\config.json"
Write-Host "Logs:       $env:LOCALAPPDATA\SyncOTP\logs"
Write-Host ''
Write-Host 'Next: set ntfy.server and ntfy.topic (and credentials, if your server needs them) in config.json, then use the tray menu to reload.'
