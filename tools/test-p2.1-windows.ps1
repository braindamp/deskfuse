# Source-only protocol/host tests. No driver installation or display mutation.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK first (dotnet.exe missing).'
}
& dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed' }
& dotnet test (Join-Path $root 'windows\agent\FocusWindow.Tests\FocusWindow.Tests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Host tests failed' }
& dotnet build (Join-Path $root 'windows\agent\FocusWindow.Agent\FocusWindow.Agent.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows host build failed' }
Write-Host 'PASS: C# tests + Windows agent compile. Driver and displays untouched.' -ForegroundColor Green
