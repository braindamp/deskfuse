# DeskFuse empirical 800x600-DIP effective-desktop boundary test.
# Creates and removes ONLY the known-good FocusIdd virtual display.
# Reads DPI caps (-3) but deliberately never writes DPI scale (-4),
# changes registry, reinstalls driver, or touches physical monitor configuration.
# No RUN / UNSUPPORTED prompts. Requires an elevated interactive Windows shell.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AgentExe,
    [string]$OutDirectory = ''
)
$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'run-dpr-challenge.ps1'
if (!(Test-Path -LiteralPath $AgentExe -PathType Leaf)) {
    throw "Agent not found: $AgentExe"
}
$AgentExe = (Resolve-Path -LiteralPath $AgentExe).Path
if (!(Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "DPR challenge runner not found: $runner"
}

# These modes distinguish the orientation-independent 600-DIP short-side floor
# from the 800-DIP long-side floor, including exact inclusive boundaries.
$cases = @(
    [pscustomobject]@{ Mode = '2100x2800'; ExpectedMax = 350; Why = '600x800 DIPs at 350%' }
    [pscustomobject]@{ Mode = '2100x2798'; ExpectedMax = 300; Why = 'Long side <800 DIPs at 350%' }
    [pscustomobject]@{ Mode = '1200x1600'; ExpectedMax = 200; Why = '600x800 DIPs at 200%' }
    [pscustomobject]@{ Mode = '1200x1598'; ExpectedMax = 175; Why = 'Long side <800 DIPs at 200%' }
    [pscustomobject]@{ Mode = '1198x1600'; ExpectedMax = 175; Why = 'Short side <600 DIPs at 200%' }
)
$modeArg = ($cases | ForEach-Object { $_.Mode }) -join ','

if (-not $OutDirectory) {
    $OutDirectory = Join-Path (Get-Location) (
        'dpr-boundary-evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$OutDirectory = (Resolve-Path -LiteralPath $OutDirectory).Path

$health = & $AgentExe driver-status
if ($LASTEXITCODE -ne 0 -or (($health -join ' ') -notmatch 'monitor arrived\s+0\b')) {
    throw 'Existing virtual monitor or unhealthy driver. Close the DeskFuse session before this experiment.'
}

Write-Host 'Testing 800x600 DIP geometry model using temporary virtual monitors.' -ForegroundColor Cyan
Write-Host 'No scaling writes, no driver reinstall, no registry writes, no interactive confirmation.'
& $runner -AgentExe $AgentExe -Run -Modes $modeArg -OutDirectory $OutDirectory
if (-not $?) {
    throw 'DPR challenge failed. Inspect the run logs and check driver-status before retrying.'
}

$evidence = Get-ChildItem -LiteralPath $OutDirectory -Filter 'DPR_CHALLENGE_*.csv' -File |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $evidence) { throw 'DPR experiment completed without a CSV evidence file.' }
$measured = @(Import-Csv -LiteralPath $evidence.FullName)
if ($measured.Count -ne $cases.Count) {
    throw "Expected $($cases.Count) evidence rows; found $($measured.Count)."
}

$results = foreach ($test in $cases) {
    $rows = @($measured | Where-Object { $_.mode -eq $test.Mode })
    if ($rows.Count -ne 1) { throw "Missing/duplicate evidence for $($test.Mode)" }
    $row = $rows[0]
    if ($row.physical_unchanged -ne 'True' -or $row.monitor_removed -ne 'True' -or $row.error) {
        throw "Display guard, monitor removal or experiment failure at $($test.Mode). Inspect $($evidence.FullName)."
    }
    $actual = 0
    if (-not [int]::TryParse($row.max, [ref]$actual)) {
        throw "Uninterpretable -3 max readback at $($test.Mode): $($row.max)"
    }
    [pscustomobject]@{
        Mode = $test.Mode
        MeasuredMaxPercent = $actual
        ModelMaxPercent = $test.ExpectedMax
        MatchesModel = ($actual -eq $test.ExpectedMax)
        Distinguishes = $test.Why
    }
}
$summary = Join-Path $OutDirectory 'boundary-summary.csv'
$results | Export-Csv -LiteralPath $summary -NoTypeInformation -Encoding UTF8
Write-Host '=== MEASURED vs PREDICTED 800x600 BOUNDARY ===' -ForegroundColor Cyan
$results | Format-Table -AutoSize
Write-Host "Evidence: $($evidence.FullName)"
Write-Host "Summary:  $summary"
$disagreements = @($results | Where-Object { -not $_.MatchesModel })
if ($disagreements.Count -gt 0) {
    Write-Warning "The 800x600 model disagrees with $($disagreements.Count) hardware observations. Revisit the hypothesis; do not mark it proven."
} else {
    Write-Host 'All five boundaries match the model. This supports but does NOT prove an OS guarantee.' -ForegroundColor Green
}
