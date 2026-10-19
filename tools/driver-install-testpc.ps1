# Compatibility entry point. Installation ALWAYS goes through the independent watchdog.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InfPath,
    [switch]$TestMachine,
    [switch]$IUnderstandDisplayDriverRisk
)
$ErrorActionPreference='Stop'
if (-not ($TestMachine -and $IUnderstandDisplayDriverRisk)) {
    throw 'Requires -TestMachine -IUnderstandDisplayDriverRisk, and the interactive safety confirmation.'
}
& (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') -InfPath $InfPath -IUnderstandDriverRisk
if ($LASTEXITCODE -ne 0) { exit 1 }
