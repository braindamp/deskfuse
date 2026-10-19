# DeskFuse DPR challenge: a short, interactive, NO-INSTALL Windows test runner.
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
    [ValidateRange(30,600)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $AgentExe -PathType Leaf)) { throw "Agent not found: $AgentExe" }
$AgentExe = (Resolve-Path -LiteralPath $AgentExe).Path
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
    $answer = Read-Host 'Type RUN to authorize the temporary virtual-display sweep'
    if ($answer -cne 'RUN') { throw 'Operator declined virtual monitor changes.' }
    if ($TryAboveCap) {
        $answer = Read-Host 'Type UNSUPPORTED to authorize the beyond-max undocumented -4 attempt'
        if ($answer -cne 'UNSUPPORTED') { throw 'Operator declined unsupported DPI write.' }
    }
}
$line = 'dpi-challenge --modes "' + $Modes + '" --diag-mm ' + $DiagonalMm +
    ' --out "' + $OutDirectory + '"'
if ($Run) { $line += ' --run --i-accept-virtual-display-test' }
if ($Try350) { $line += ' --try-350' }
if ($TryAboveCap) { $line += ' --try-above-cap --i-accept-unsupported-scale-risk' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdout = Join-Path $OutDirectory "runner-$stamp.stdout.txt"
$stderr = Join-Path $OutDirectory "runner-$stamp.stderr.txt"
Write-Host "Agent: $AgentExe"
Write-Host "Arguments: $line"
Write-Host "Evidence: $OutDirectory"
$child = Start-Process -FilePath $AgentExe -ArgumentList $line -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
    try { $child.Kill() } catch { }
    Write-Warning "Hard timeout. Process killed. FocusIdd's existing heartbeat watchdog should remove its virtual monitor after approximately 10 seconds."
    Write-Warning 'Check your physical displays and driver-status before any further test.'
    throw "DPR challenge exceeded $TimeoutSeconds seconds. Inspect $stdout / $stderr."
}
$child.Refresh()
Get-Content -LiteralPath $stdout
if ((Test-Path $stderr) -and ((Get-Item $stderr).Length -gt 0)) {
    Write-Warning "Stderr log: $stderr"
    Get-Content -LiteralPath $stderr
}
if ($child.ExitCode -ne 0) {
    throw "Challenge returned $($child.ExitCode). Check evidence and confirm virtual monitor removed."
}
Write-Host 'Challenge ended. Review CSV/JSON readbacks; green CI alone is NOT a hardware success.' -ForegroundColor Green
