# Read-only geometry/EDID experiment for the FocusIdd virtual display.
# Deliberately does not attempt -4 DPI writes, alter physical displays, install drivers,
# modify registry values, or use the unsupported scaling override.
# Requires the known-good FocusIdd driver and elevated interactive shell.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AgentExe,
    [string]$Modes = '1440x3120,1440x3900,1600x3120',
    [int[]]$DiagonalsMm = @(80,170,420),
    [string]$OutDirectory = '',
    [ValidateRange(30,600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'run-dpr-challenge.ps1'
if (!(Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Challenge runner missing: $runner" }
if (!(Test-Path -LiteralPath $AgentExe -PathType Leaf)) { throw "Agent missing: $AgentExe" }
$AgentExe = (Resolve-Path -LiteralPath $AgentExe).Path
if ($DiagonalsMm.Count -lt 1 -or $DiagonalsMm.Count -gt 5) {
    throw 'Supply 1–5 EDID diagonals.'
}
foreach ($d in $DiagonalsMm) {
    if ($d -lt 60 -or $d -gt 500) { throw "Invalid EDID diagonal $d mm" }
}
if (-not $OutDirectory) {
    $OutDirectory = Join-Path (Get-Location) 'dpr-geometry-evidence'
}
New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null
$OutDirectory = (Resolve-Path -LiteralPath $OutDirectory).Path
$status = & $AgentExe driver-status
if ($LASTEXITCODE -ne 0 -or (($status -join ' ') -notmatch 'monitor arrived\s+0\b')) {
    throw 'Existing FocusIdd monitor or unhealthy driver; refusing the geometry scan.'
}
Write-Host 'READ-ONLY scaling-cap investigation: no DPI SET attempts.' -ForegroundColor Cyan
Write-Host 'Each EDID physical size gets a distinct stable VIRTUAL display serial.' -ForegroundColor Cyan
Write-Host "Modes: $Modes ; EDID diagonals: $($DiagonalsMm -join ',') mm"
$rows = @()
foreach ($d in $DiagonalsMm) {
    Write-Host "== EDID diagonal: $d mm ==" -ForegroundColor Cyan
    & $runner -AgentExe $AgentExe -Modes $Modes -DiagonalMm $d `
        -OutDirectory $OutDirectory -Run -IsolateDiagIdentity -TimeoutSeconds $TimeoutSeconds
    if ($LASTEXITCODE -ne 0 -or -not $?) {
        throw "Aborted at diagonal $d mm. Inspect evidence and driver-status before another test."
    }
    $file = Get-ChildItem -LiteralPath $OutDirectory -Filter 'DPR_CHALLENGE_*.csv' -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $file) { throw "Missing CSV evidence after $d mm test." }
    foreach ($item in (Import-Csv -LiteralPath $file.FullName)) {
        $rows += [pscustomobject]@{
            DiagonalMm = $d
            Mode = $item.mode
            MaxPercent = $item.max
            RecommendedPercent = $item.recommended
            CurrentPercent = $item.initial
            MonitorDpi = $item.monitor_dpi
            WindowDpi = $item.window_dpi
            PhysicalUnchanged = $item.physical_unchanged
            MonitorRemoved = $item.monitor_removed
        }
    }
}
Write-Host '=== Geometry / EDID cap comparison ===' -ForegroundColor Cyan
$rows | Format-Table -AutoSize
$summaryPath = Join-Path $OutDirectory 'geometry-summary.csv'
$rows | Export-Csv -Path $summaryPath -NoTypeInformation -Encoding UTF8
Write-Host "Summary: $summaryPath" -ForegroundColor Green
