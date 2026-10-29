# Compatibility entrypoint: no automatic package approval or privileged bootstrap.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][Guid]$NamespaceId, [Parameter(Mandatory=$true)][Guid]$RunId,
    [switch]$TestMachine, [switch]$IUnderstandDisplayDriverRisk)
$ErrorActionPreference='Stop'
if (-not ($TestMachine -and $IUnderstandDisplayDriverRisk)) { throw 'Requires -TestMachine -IUnderstandDisplayDriverRisk and the interactive prepared-run confirmation.' }
& (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') -NamespaceId $NamespaceId -RunId $RunId -IUnderstandDriverRisk
