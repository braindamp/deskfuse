# Exact prepared-run recovery only. No device enumeration sweep or package deletion.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][Guid]$NamespaceId,
    [Parameter(Mandatory=$true)][Guid]$RunId,
    [ValidateRange(0,60)][int]$WaitSeconds=25,
    [switch]$Quiet
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'maintenance-runner.psm1') -Force -DisableNameChecking
$session=Open-DeskFusePreparedSession $NamespaceId $RunId $false
function Test-OwnerRetirement {
    if (Test-DeskFuseRecoveryRetired $session) { return $true }
    $status=Get-DeskFuseWatchStatus $session.RunDirectory
    return ((Update-DeskFuseRunner $session $status (Get-DeskFuseUptime)) -eq 'Retired')
}
if (Test-OwnerRetirement) { if (-not $Quiet) { Write-Host 'SYSTEM owner already reports this exact run Retired.' }; return }
Start-DeskFusePreparedTask $session $true
$until=(Get-DeskFuseUptime) + $WaitSeconds * 1000
while ((Get-DeskFuseUptime) -lt $until) {
    if (Test-OwnerRetirement) { if (-not $Quiet) { Write-Host 'SYSTEM owner reports this exact run Retired.' }; return }
    Start-Sleep -Milliseconds 200
}
throw 'Retirement remains unconfirmed. Exact recovery tasks and durable history are retained; do not start another run or sweep legacy devices.'
