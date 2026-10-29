# Compatibility request only. The prepared Scheduler task executes the native owner directly.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][Guid]$NamespaceId, [Parameter(Mandatory=$true)][Guid]$RunId)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'maintenance-runner.psm1') -Force -DisableNameChecking
$session=Open-DeskFusePreparedSession $NamespaceId $RunId $false
Start-DeskFusePreparedTask $session $false
Write-Host 'Exact prepared watchdog start requested. This message does not certify arming or installation.'
