# Prepared-run operator entrypoint. All native authority remains in the SYSTEM owner.
[CmdletBinding()]
param(
    [Guid]$NamespaceId = [Guid]::Empty,
    [Guid]$RunId = [Guid]::Empty,
    [switch]$DescribeRunner,
    [switch]$IUnderstandDriverRisk,
    [string]$InfPath = ''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($DescribeRunner) {
    $current=[Diagnostics.Process]::GetCurrentProcess()
    try { [ordered]@{ pid=$current.Id; creationFileTime=$current.StartTime.ToUniversalTime().ToFileTimeUtc(); role='Runner'; authorizesInstallation=$false } | ConvertTo-Json -Compress }
    finally { $current.Dispose() }
    return
}
if ($InfPath -or $NamespaceId -eq [Guid]::Empty -or $RunId -eq [Guid]::Empty) {
    throw 'Use namespace/run identities from independently approved trusted setup. An INF path or build receipt cannot authorize preparation or installation.'
}
if (-not $IUnderstandDriverRisk) { throw 'Explicit -IUnderstandDriverRisk is required.' }
Import-Module (Join-Path $PSScriptRoot 'maintenance-runner.psm1') -Force -DisableNameChecking
$session=Open-DeskFusePreparedSession $NamespaceId $RunId $true
Assert-DeskFuseInteractiveOperator
Write-Host 'This temporary display-driver proof can disrupt displays or crash Windows. Recovery requires Windows to remain responsive.' -ForegroundColor Yellow
$expected='START PREPARED FOCUSIDD RUN ' + $RunId.ToString('N')
if ((Read-Host "Type $expected").Trim() -cne $expected) { throw 'Cancelled before starting the prepared watchdog.' }
try {
    if (-not (Invoke-DeskFuseGuardedRunner $session)) { throw 'Proof did not complete all three accepted checkpoints. Inspect the retained run and recovery result.' }
    Write-Host 'Three checkpoints accepted; the SYSTEM owner reported this exact run Retired. Density evidence is recorded separately.' -ForegroundColor Green
    Write-Host "Evidence and owner records: $($session.RunDirectory)"
} catch { Write-Error $_; exit 3 }
