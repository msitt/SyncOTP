<#
.SYNOPSIS
    Replaces an installed copy of SyncOTP with the contents of a payload directory.

.DESCRIPTION
    The one implementation of the risky part of installing: stop the running instance, wait for it
    to let go of its own exe, back up what is there, swap in the new files, and relaunch. Both
    scripts/install.ps1 (payload = a fresh dotnet publish) and the in-app updater (payload = an
    extracted release zip) call this, so the sequence is exercised on every local install rather
    than only on a user's machine during an update.

    Config (%APPDATA%\SyncOTP) and logs (%LOCALAPPDATA%\SyncOTP) are never touched. Neither is the
    registry.

.PARAMETER Source
    Directory holding the new files. Its contents are copied to -Target, not mirrored, so anything
    extra already in the install directory survives.

.PARAMETER Target
    The install directory to replace.

.PARAMETER Exe
    Full path to SyncOTP.exe inside -Target. Used for the lock probe and the relaunch.

.PARAMETER ProcessId
    PID of the instance to wait for. When omitted, any running SyncOTP process is used. Deliberately
    not named -Pid: $PID is a read-only automatic variable and param([int]$Pid) fails at runtime.

.PARAMETER Backup
    Directory to copy the current install into before replacing it. Kept afterwards, so a build that
    will not start can be rolled back by hand.

.PARAMETER ForceStop
    Kill the running instance if it does not exit on request. install.ps1 passes this, because the
    user explicitly asked for an install. The updater does not: a graceful exit that never arrives
    means the user is still using the app, and taking it away to install an update they did not ask
    to apply right now is worse than not updating.

.PARAMETER RemoveSource
    Delete -Source when finished, and allow the faster directory-swap path. The updater passes this
    (its payload is disposable staging). install.ps1 does not, since its payload is build output.

.PARAMETER RelaunchArgs
    Arguments for the relaunched exe.

.PARAMETER NoLaunch
    Do not relaunch afterwards.

.PARAMETER Log
    Append a transcript here. Defaults to %LOCALAPPDATA%\SyncOTP\logs\update.log.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Target,
    [string]   $Exe,
    [int]      $ProcessId = 0,
    [string]   $Backup,
    [switch]   $ForceStop,
    [switch]   $RemoveSource,
    [string[]] $RelaunchArgs = @(),
    [switch]   $NoLaunch,
    [string]   $Log
)

$ErrorActionPreference = 'Stop'

if (-not $Exe) { $Exe = Join-Path $Target 'SyncOTP.exe' }
if (-not $Log) { $Log = Join-Path $env:LOCALAPPDATA 'SyncOTP\logs\update.log' }

$ExitEventName    = 'Local\SyncOTP.ExitRequest'
$ExitTimeoutMs    = 30000
$LockRetries      = 20
$LockRetryDelayMs = 250

function Write-Log {
    param([string] $Message, [string] $Level = 'INFO')

    $line = '{0} [{1,-5}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    try {
        $dir = Split-Path -Parent $Log
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -Path $Log -Value $line -Encoding UTF8
    } catch {
        # Logging must never be the reason an update fails.
    }
    Write-Host $line
}

function Invoke-Relaunch {
    # The caller has usually exited by the time we get here, so every path that gets this far has
    # to put something back. Leaving the user with no running app is the worst outcome available.
    if ($NoLaunch) { return }

    Start-Sleep -Milliseconds 500

    if (-not (Test-Path -LiteralPath $Exe)) {
        Write-Log "nothing to relaunch, $Exe is missing" 'ERROR'
        return
    }

    try {
        if ($RelaunchArgs -and $RelaunchArgs.Count -gt 0) {
            Start-Process -FilePath $Exe -ArgumentList $RelaunchArgs
        } else {
            Start-Process -FilePath $Exe
        }
        Write-Log "relaunched $Exe"
    } catch {
        Write-Log "relaunch failed: $($_.Exception.Message)" 'ERROR'
    }
}

# A broken invocation is otherwise impossible to diagnose after the fact.
Write-Log "apply-payload starting"
Write-Log "  Source=$Source"
Write-Log "  Target=$Target"
Write-Log "  Exe=$Exe  ProcessId=$ProcessId  ForceStop=$ForceStop  RemoveSource=$RemoveSource"

if (-not (Test-Path -LiteralPath $Source)) {
    Write-Log "source directory does not exist" 'ERROR'
    exit 1
}

# ---- 1. ask the running instance to exit ----------------------------------------------------

function Get-TargetProcesses {
    if ($ProcessId -gt 0) {
        return @(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
    }
    return @(Get-Process -Name 'SyncOTP' -ErrorAction SilentlyContinue)
}

$running = Get-TargetProcesses

if ($running.Count -gt 0) {
    Write-Log "waiting for $($running.Count) running instance(s) to exit"

    # Only ask an instance we did not launch. When the app starts this itself it passes its own
    # -ProcessId and is already shutting down, so a request would arrive with nobody left to take
    # it. A named event stays signalled until something consumes it, and the next thing to open it
    # is the version we just installed, which would read it as "exit" and shut down on the spot.
    # That leaves the user with no running app, which is the whole thing this script exists to
    # avoid.
    if ($ProcessId -le 0) {
        # The app clears the clipboard and tears down its toast registration on a real exit, none
        # of which happens if it is killed, so always ask first.
        try {
            $handle = [System.Threading.EventWaitHandle]::OpenExisting($ExitEventName)
            $handle.Set() | Out-Null
            $handle.Dispose()
            Write-Log "exit requested via $ExitEventName"
        } catch {
            Write-Log "no exit-request handle, the instance predates it or is already going: $($_.Exception.Message)" 'WARN'
        }
    } else {
        Write-Log "waiting for pid $ProcessId to exit on its own"
    }

    foreach ($p in $running) {
        if (-not $p.WaitForExit($ExitTimeoutMs)) {
            if ($ForceStop) {
                Write-Log "instance $($p.Id) did not exit in time, stopping it" 'WARN'
                try { Stop-Process -Id $p.Id -Force } catch { Write-Log "stop failed: $($_.Exception.Message)" 'ERROR' }
                Start-Sleep -Milliseconds 700
            } else {
                # Nothing has been touched yet, so abandoning here is completely safe.
                Write-Log "instance $($p.Id) is still running, abandoning without changing anything" 'ERROR'
                exit 1
            }
        }
    }

    Write-Log "all instances have exited"
} else {
    Write-Log "no running instance"
}

# ---- 2. wait for the image lock to clear ------------------------------------------------------

# Process exit and the OS releasing the executable image are not the same instant, and antivirus
# will often hold a just-written exe open to scan it.
if (Test-Path -LiteralPath $Exe) {
    $unlocked = $false
    for ($i = 0; $i -lt $LockRetries; $i++) {
        try {
            $stream = [System.IO.File]::Open($Exe, 'Open', 'ReadWrite', 'None')
            $stream.Dispose()
            $unlocked = $true
            break
        } catch {
            Start-Sleep -Milliseconds $LockRetryDelayMs
        }
    }

    if ($unlocked) {
        Write-Log "exe is unlocked"
    } else {
        Write-Log "exe still locked after $($LockRetries * $LockRetryDelayMs) ms, continuing anyway" 'WARN'
    }
}

# ---- 3. back up the current install -----------------------------------------------------------

$backedUp = $false

if ($Backup -and (Test-Path -LiteralPath $Target)) {
    try {
        if (Test-Path -LiteralPath $Backup) { Remove-Item -LiteralPath $Backup -Recurse -Force }
        New-Item -ItemType Directory -Path $Backup -Force | Out-Null
        Copy-Item -Path (Join-Path $Target '*') -Destination $Backup -Recurse -Force
        $backedUp = $true
        Write-Log "backed up the current install to $Backup"
    } catch {
        # Without a backup there is no way back, so this is the last moment it is still safe to
        # stop. Nothing in the target has been touched yet, and a copy that fails halfway with
        # nothing to restore from is far worse than an update that did not happen.
        Write-Log "backup failed, abandoning without changing anything: $($_.Exception.Message)" 'ERROR'

        # The app that asked for this has already exited, so put the old one back before giving up.
        Invoke-Relaunch
        exit 1
    }
}

# ---- 4. apply -----------------------------------------------------------------------------------

$applied = $false

try {
    if (-not (Test-Path -LiteralPath $Target)) {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
    }

    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
    $applied = $true
    Write-Log "payload copied to $Target"
} catch {
    Write-Log "copy failed: $($_.Exception.Message)" 'ERROR'

    if ($backedUp) {
        try {
            Copy-Item -Path (Join-Path $Backup '*') -Destination $Target -Recurse -Force
            Write-Log "restored the previous install from $Backup" 'WARN'
        } catch {
            # Both the update and the rollback are gone. Say so as loudly as a log file allows.
            Write-Log "ROLLBACK FAILED. The install at $Target may be incomplete." 'ERROR'
            Write-Log "A copy of the previous install is at $Backup. Copy it back by hand." 'ERROR'
        }
    }
}

# ---- 5. relaunch, whatever happened above -------------------------------------------------------

# Not conditional on the copy having worked: a failed update that leaves the old version running is
# a bad day, a failed update that leaves nothing running is a support ticket.
Invoke-Relaunch

# ---- 6. clean up --------------------------------------------------------------------------------

# The backup is deliberately kept. It is the only way back for someone whose new build will not
# start, and it costs one copy of the install directory until the next update replaces it.
if ($RemoveSource -and $applied) {
    try {
        Remove-Item -LiteralPath $Source -Recurse -Force
        Write-Log "removed the staging directory"
    } catch {
        Write-Log "could not remove $($Source): $($_.Exception.Message)" 'WARN'
    }
}

if ($applied) {
    Write-Log "apply-payload finished"
    exit 0
}

Write-Log "apply-payload finished with errors" 'ERROR'
exit 1
