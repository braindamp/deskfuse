# Run on Windows with VS 2022 + matching WDK; compilation is not driver installation.
[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$Platform = 'x64')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'windows\driver\FocusIdd\FocusIdd.vcxproj'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $vsRoot = (& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1)
    if ($vsRoot) {
        # Prefer the explicit 64-bit executable for WDK build tasks.
        $msbuild64 = Join-Path $vsRoot 'MSBuild\Current\Bin\amd64\MSBuild.exe'
        $msbuildDefault = Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe'
        if (Test-Path $msbuild64) { $msbuild = $msbuild64 }
        elseif (Test-Path $msbuildDefault) { $msbuild = $msbuildDefault }
    }
}
if (-not $msbuild) {
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { $msbuild = $cmd.Source }
}
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio 2022 and WDK.' }
Write-Host "== Driver build with $msbuild =="
& $msbuild $project /m /t:Build "/p:Configuration=$Configuration" "/p:Platform=$Platform" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'Driver build FAILED; check actual WDK/IddCx errors. Do not install.' }
Write-Host 'Build command succeeded. Inspect generated INF/DLL/CAT and validate driver signing before installing.'
