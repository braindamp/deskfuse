# Direct density probing is intentionally gated behind the independent safety watchdog.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InfPath,
    [string]$AgentExe = '',
    [int]$Width = 1440, [int]$Height = 3120,
    [double]$Dpr = 3.5, [int]$DiagonalMm = 170,
    [int]$Refresh = 60,
    [switch]$IUnderstandDriverRisk
)
$ErrorActionPreference='Stop'
if (-not $IUnderstandDriverRisk) { throw 'Explicit -IUnderstandDriverRisk required.' }
$argsSafe = @{ InfPath=$InfPath; Width=$Width; Height=$Height; Dpr=$Dpr; DiagonalMm=$DiagonalMm; Refresh=$Refresh; IUnderstandDriverRisk=$true }
if ($AgentExe) { $argsSafe.AgentExe = $AgentExe }
& (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') @argsSafe
if ($LASTEXITCODE -ne 0) { exit 1 }
