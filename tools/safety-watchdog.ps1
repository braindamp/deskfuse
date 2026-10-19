# Runs independently under Task Scheduler as SYSTEM, started BEFORE driver installation.
# One acknowledgement is due at 30 seconds, with a 15-second response window.
# The watchdog also has an absolute timeout even if the user acknowledges.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SafetyDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-UptimeMs {
    return [long]([Diagnostics.Stopwatch]::GetTimestamp() * 1000.0 / [Diagnostics.Stopwatch]::Frequency)
}
function Log([string]$message) {
    Add-Content -LiteralPath (Join-Path $SafetyDirectory 'watchdog.log') -Value "$(Get-Date -Format o) $message" -ErrorAction SilentlyContinue
}
function Mark([string]$name) {
    $path = Join-Path $SafetyDirectory $name
    Set-Content -LiteralPath ($path + '.tmp') -Value (Get-Date -Format o) -Encoding Ascii
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
}
function Has([string]$name) { Test-Path -LiteralPath (Join-Path $SafetyDirectory $name) }

try {
    $rollback = Join-Path $SafetyDirectory 'rollback-focusidd.ps1'
    if (-not (Test-Path -LiteralPath $rollback)) { throw 'Rollback implementation missing.' }
    $state = Get-Content -LiteralPath (Join-Path $SafetyDirectory 'state.json') -Raw | ConvertFrom-Json
    $start = [long]$state.StartTick
    Mark 'armed.flag'
    Log "Armed (id $($state.Id), start tick $start)"
    $reason = ''
    while ($true) {
        $elapsed = [long]((Get-UptimeMs) - $start)
        if (Has 'done.flag') { Log 'Completed and device removed'; exit 0 }
        if (Has 'abort.flag') { $reason = 'Foreground requested rollback'; break }
        if ($elapsed -ge 50000 -and -not (Has 'ack-1.flag')) {
            $reason = 'Safety confirmation missing after 50 seconds'
        }
        # Acknowledgement must NOT disable independent automatic cleanup.
        # This catches a hung foreground runner even after a successful click.
        if ($elapsed -ge 75000) {
            $reason = 'Absolute 75-second safety timeout'
        }
        if ($reason) { break }
        Start-Sleep -Seconds 1
    }
    Log "ROLLBACK: $reason"
    Mark 'rollback-started.flag'
    # Remain active for 20 minutes: a stalled installation can finish AFTER the first removal.
    # Only the unique ROOT\FocusIdd hardware ID is affected.
    $end = (Get-UptimeMs) + 20 * 60 * 1000
    while ((Get-UptimeMs) -lt $end) {
        if (Has 'done.flag') { Log 'Confirmed clean; ending watchdog'; exit 0 }
        try {
            & $rollback -Quiet 2>&1 | ForEach-Object { Log "rollback: $_" }
            Mark 'rollback-clean.flag'
        } catch {
            Log "rollback error: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 4
    }
    Log 'End of rollback window; manual inspection required if device remains.'
    exit 1
} catch {
    Log "WATCHDOG ERROR: $($_.Exception.Message)"
    try {
        & (Join-Path $SafetyDirectory 'rollback-focusidd.ps1') -Quiet
    } catch { Log "emergency rollback error: $($_.Exception.Message)" }
    exit 2
}
