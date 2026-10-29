# Compatibility request only. The prepared boot task runs the remove-only native owner.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][Guid]$NamespaceId, [Parameter(Mandatory=$true)][Guid]$RunId)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'maintenance-runner.psm1') -Force -DisableNameChecking
$session=Open-DeskFusePreparedSession $NamespaceId $RunId $false
Start-DeskFusePreparedTask $session $true
Write-Host 'Exact remove-only recovery requested. Retirement must be independently confirmed by the owner.'
