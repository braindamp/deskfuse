# Run in Windows PowerShell / pwsh on a development PC with .NET 8 SDK.
[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$agent = Join-Path $root 'windows\agent\FocusWindow.Agent\FocusWindow.Agent.csproj'
$tests = Join-Path $root 'windows\agent\FocusWindow.Tests\FocusWindow.Tests.csproj'
$out = Join-Path $root 'out\host'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Missing .NET 8 SDK (dotnet executable).' }
Write-Host '== .NET SDK ==' ; dotnet --info
Write-Host '== Build Core/Agent =='
& dotnet build $agent -c Release -p:Platform=x64 -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Host build FAILED' }
if (-not $SkipTests) {
    Write-Host '== Generate contract with MSVC x64 (not Linux shims) =='
    & (Join-Path $PSScriptRoot 'build-native-contract.ps1')
    $env:FOCUS_CONTRACT_FILE = Join-Path $root 'out\native-contract\contract.windows.txt'
    Write-Host '== Host tests =='
    & dotnet test $tests -c Release --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Host tests FAILED' }
}
Write-Host '== Publish Windows x64 host =='
& dotnet publish $agent -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'Host publish FAILED' }
Write-Host "PASS: $out\FocusWindow.Agent.exe"
