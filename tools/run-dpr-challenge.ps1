# DeskFuse DPR challenge: non-prompting, explicitly flagged, NO-INSTALL Windows test runner.
# Never invokes pnputil, devcon, DisplayConfig on physical monitors, or registry edits.
# Does NOT replace tools/safe-driver-proof.ps1 (driver install / independent watchdog).
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AgentExe,
    [string]$Modes = '1440x3120,1600x3120,1920x3120,2100x3120,2240x3120,2240x3360',
    [ValidateRange(60,500)][int]$DiagonalMm = 170,
    [string]$OutDirectory = '',
    [switch]$Run,
    [switch]$Try350,
    [switch]$TryAboveCap,
    [ValidateRange(500,30000)][int]$SettleMs = 1500,
    [string]$NativeWitnessExe = '',
    [ValidateRange(30,600)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $AgentExe -PathType Leaf)) { throw "Agent not found: $AgentExe" }
$AgentExe = (Resolve-Path -LiteralPath $AgentExe).Path
if ($NativeWitnessExe) {
    if (!(Test-Path -LiteralPath $NativeWitnessExe -PathType Leaf)) { throw "Native witness not found: $NativeWitnessExe" }
    $NativeWitnessExe = (Resolve-Path -LiteralPath $NativeWitnessExe).Path
    if ($NativeWitnessExe.Contains('"')) { throw 'Native witness path contains an unsupported quote character.' }
}
if ($Modes -notmatch '^\d{3,4}x\d{3,4}(,\d{3,4}x\d{3,4}){0,9}$') {
    throw 'Modes must be a comma-separated list of at most 10 WIDTHxHEIGHT entries.'
}
if (-not $OutDirectory) { $OutDirectory = Join-Path (Get-Location) 'dpr-challenge-evidence' }
New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null
$OutDirectory = (Resolve-Path -LiteralPath $OutDirectory).Path
if ($OutDirectory.Contains('"')) { throw 'Output directory contains an unsupported quote character.' }
if ($TryAboveCap -and -not $Try350) { throw '-TryAboveCap requires -Try350.' }
if ($Run) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Hardware sweep requires an elevated, interactive Windows shell.'
    }
    if (-not [Environment]::UserInteractive) {
        throw 'Hardware sweep must run in an interactive Windows session.'
    }
    Write-Host 'RISK: This test creates/removes ONLY an existing FocusIdd virtual monitor.' -ForegroundColor Yellow
    Write-Host 'There is no driver reinstall. Unexpected Windows display behavior is possible.'
    Write-Host 'The installed driver has its own 10-second no-heartbeat monitor teardown.'
    Write-Host 'There is NO independent SYSTEM rollback in this lab; use only with the known-good driver.'
    Write-Host 'Close any DeskFuse streaming session before starting.'
    # No interactive confirmation: explicit -Run / -Try350 / -TryAboveCap
    # switches authorize only this process's guarded virtual-monitor experiment.
    # The agent still refuses an existing FocusIdd monitor and verifies physical displays.
    Write-Host 'Explicit flags accepted; proceeding without interactive confirmation.'
}
$line = 'dpi-challenge --modes "' + $Modes + '" --diag-mm ' + $DiagonalMm +
    ' --out "' + $OutDirectory + '" --settle-ms ' + $SettleMs
if ($Run) { $line += ' --run --i-accept-virtual-display-test' }
if ($NativeWitnessExe) { $line += ' --native-witness "' + $NativeWitnessExe + '"' }
if ($Try350) { $line += ' --try-350' }
if ($TryAboveCap) { $line += ' --try-above-cap --i-accept-unsupported-scale-risk' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdout = Join-Path $OutDirectory "runner-$stamp.stdout.txt"
$stderr = Join-Path $OutDirectory "runner-$stamp.stderr.txt"
Write-Host "Agent: $AgentExe"
Write-Host "Arguments: $line"
Write-Host "Evidence: $OutDirectory"
# Use System.Diagnostics.Process directly: Start-Process -PassThru can leave
# ExitCode null on Windows PowerShell 5.1 even after WaitForExit() succeeds.
# Read both streams asynchronously so output cannot deadlock a long sweep.
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $AgentExe
$startInfo.Arguments = $line
$startInfo.WorkingDirectory = (Get-Location).Path
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
$child = New-Object System.Diagnostics.Process
$child.StartInfo = $startInfo
try {
    if (-not $child.Start()) { throw 'Could not start the DeskFuse challenge agent.' }
    $outputTask = $child.StandardOutput.ReadToEndAsync()
    $errorTask = $child.StandardError.ReadToEndAsync()
    $completed = $child.WaitForExit($TimeoutSeconds * 1000)
    if (-not $completed) {
        try { $child.Kill() } catch { }
        Write-Warning "Hard timeout. Process killed. FocusIdd's watchdog should remove its virtual monitor after approximately 10 seconds."
        Write-Warning 'Check physical displays and driver-status before any further test.'
        throw "DPR challenge exceeded $TimeoutSeconds seconds. Confirm virtual monitor removal."
    }
    # The process has exited; unlike Start-Process -PassThru, ExitCode is reliable.
    $code = [int]$child.ExitCode
    $outputText = $outputTask.GetAwaiter().GetResult()
    $errorText = $errorTask.GetAwaiter().GetResult()
    [System.IO.File]::WriteAllText($stdout, $outputText, ([System.Text.UTF8Encoding]::new($false)))
    [System.IO.File]::WriteAllText($stderr, $errorText, ([System.Text.UTF8Encoding]::new($false)))
    if ($outputText) { Write-Host $outputText }
    if ($errorText) { Write-Warning "Stderr log: $stderr"; Write-Host $errorText }
    if ($code -ne 0) {
        throw "Challenge returned exit code $code. Check evidence and confirm virtual monitor removed."
    }
    Write-Host 'Challenge ended successfully. The measurements may still show DPI below target.' -ForegroundColor Green
}
finally { $child.Dispose() }
