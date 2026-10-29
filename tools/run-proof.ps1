# Direct proof always uses the independently prepared three-checkpoint runner.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][Guid]$NamespaceId, [Parameter(Mandatory=$true)][Guid]$RunId, [switch]$IUnderstandDriverRisk)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') -NamespaceId $NamespaceId -RunId $RunId -IUnderstandDriverRisk:$IUnderstandDriverRisk
