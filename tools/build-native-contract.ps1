# Build the C++ driver<->host ABI contract with REAL MSVC x64 Windows headers.
# Invoke from an x64 Native Tools / WDK developer PowerShell (cl.exe on PATH).
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$test = Join-Path $root 'windows\driver\tests\contract_test.cpp'
$outdir = Join-Path $root 'out\native-contract'
New-Item -ItemType Directory -Force -Path $outdir | Out-Null
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    throw 'cl.exe missing. Open x64 Native Tools Command Prompt for VS 2022, run pwsh/powershell there.'
}
$exe = Join-Path $outdir 'contract.exe'
Push-Location $outdir
try {
    & cl.exe /nologo /std:c++17 /W4 /EHsc $test "/Fe:$exe"
    if ($LASTEXITCODE -ne 0) { throw 'Native Windows C++ ABI contract build failed.' }
    $lines = & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Native Windows contract executable failed.' }
    if (($lines | Measure-Object -Line).Lines -ne 41) {
        throw 'Expected exactly 41 ABI constants from C++, investigate header drift.'
    }
    $result = Join-Path $outdir 'contract.windows.txt'
    $lines | Set-Content $result -Encoding ascii
    Write-Host "PASS: MSVC x64 native ABI contract: $result"
    $driverTestSource = Join-Path $root 'tools\driver-test-abi.cpp'
    $driverTestExe = Join-Path $outdir 'driver-test-abi.exe'
    & cl.exe /nologo /std:c++17 /W4 /WX /EHsc $driverTestSource "/Fe:$driverTestExe"
    if ($LASTEXITCODE -ne 0) { throw 'Native driver-test SetupAPI ABI build failed.' }
    $driverTestLines = & $driverTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Native driver-test ABI executable failed.' }
    $driverTestResult = Join-Path $outdir 'driver-test-abi.json'
    $driverTestLines | Set-Content -LiteralPath $driverTestResult -Encoding ascii
    if (($driverTestLines -join "`n" | ConvertFrom-Json).pointer_size -ne 8) {
        throw 'Native driver-test ABI was not compiled for x64.'
    }
    Write-Host "PASS: MSVC x64 driver-test ABI: $driverTestResult"
    $env:DESKFUSE_DRIVER_TEST_ABI_FILE = $driverTestResult
}
finally { Pop-Location }
