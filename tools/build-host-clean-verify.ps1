# DeskFuse host v0.2.29: verify patched sources, force full host recompilation, then verify executable.
# Run in x64 Native Tools Command Prompt: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\build-host-clean-verify.ps1
# Does not install a driver or modify display configuration.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$expectedId = 'DeskFuse-HoldUntilOK-0.2.29'
$expected = @{
    'tools\safe-driver-proof.ps1' = '06C7B7413B2340802C0B1EE1F64357FC5C6E2E651FDFAF997DDE75FD1891BF39'
    'windows\agent\FocusWindow.Agent\Display\DisplayConfigApi.cs' = '91E9169AB8DA603003F31FC318F016F6608589047110690E61A4DAC3FC0F22BF'
    'windows\agent\FocusWindow.Agent\Interop\Native.cs' = '259058DBC27636F2816A81233E12931746063633FE1328A0893096B0DC8BBAD5'
    'windows\agent\FocusWindow.Agent\Program.cs' = '1067301272BC7E6661D52D96503A3F96FDD71F38EB09BF6D43887A45A32C4E6F'
    'windows\agent\FocusWindow.Core\Display\DensityNegotiator.cs' = '0D0E2848DAB51AA1B71E73CE834C63EE53435B2F3055F52085038A0BCA282148'
    'windows\agent\FocusWindow.Core\Display\InteropStructs.cs' = 'F275B78FE45AB305F1C83D53133476E5293B360F3DF21B0123CEFC3CCB9CC7C9'
    'windows\agent\FocusWindow.Tests\InteropLayoutTests.cs' = '498E3B7EF9C5933134189E5EE31ECC1E5E3EF3E59310D58DBF8CA50578C1B7D8'
    'windows\agent\FocusWindow.Tests\DensityTests.cs' = '5498F96C4070616E68A11D7AA3FBCE384DB6A0FFF5442CA89C2A3927571C7B7F'
}
foreach ($relative in $expected.Keys) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "PATCH FILE MISSING: $path. Extract the v0.2.29 ZIP directly over the project root." }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $expected[$relative]) { throw "PATCH FILE MISMATCH: $path. Replace it from the v0.2.29 ZIP and retry. Expected $($expected[$relative]); actual $actual" }
    Write-Host "VERIFIED: $relative"
}
# Remove ONLY generated .NET build directories. ZIP extraction may preserve an old source
# timestamp, so a normal incremental dotnet build can reuse v0.2.24 assemblies.
$generated = @(
    'windows\agent\FocusWindow.Agent\obj',
    'windows\agent\FocusWindow.Agent\bin',
    'windows\agent\FocusWindow.Core\obj',
    'windows\agent\FocusWindow.Core\bin',
    'out\host'
)
foreach ($relative in $generated) {
    $path = Join-Path $root $relative
    if (Test-Path -LiteralPath $path) {
        Write-Host "Removing generated output: $relative"
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
    }
}
$build = Join-Path $root 'tools\build-host.ps1'
Write-Host 'Building verified host and running all tests (native resolution and 30-second hold gate)...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build
if ($LASTEXITCODE -ne 0) { throw "build-host.ps1 failed with exit code $LASTEXITCODE" }
$exe = Join-Path $root 'out\host\FocusWindow.Agent.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Published EXE missing: $exe" }
$actualId = ((& $exe '--probe-build-id' 2>&1) -join "`n").Trim()
if ($LASTEXITCODE -ne 0 -or $actualId -ne $expectedId) {
    throw "WRONG EXECUTABLE: expected '$expectedId', got '$actualId'. STOP; do not install the driver."
}
Write-Host "PASS: $actualId" -ForegroundColor Green
Write-Host 'Driver-free hold-gate smoke test: proof cannot release early...'
$gateOutput = @(& $exe 'hold-gate-self-test' 2>&1)
if ($LASTEXITCODE -ne 0 -or (($gateOutput -join "`n") -notmatch 'PASS: release requires OK flag')) {
    throw "HOLD-GATE SMOKE TEST FAILED. DO NOT install driver: $($gateOutput -join ' ')"
}
Write-Host ($gateOutput -join "`n") -ForegroundColor Green
Write-Host 'Read-only Windows display-path diagnostic:'
& $exe 'display-paths'
if ($LASTEXITCODE -ne 0) { throw "display-paths failed with exit code $LASTEXITCODE" }
Write-Host 'PASS: clean build, host tests, exact EXE, hold gate and CCD diagnostic. Driver not installed.' -ForegroundColor Green
