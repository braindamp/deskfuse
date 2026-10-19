[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$pocRoot = Join-Path $repo "windows\poc\gpu-phone-workspace"
$agentProject = Join-Path $repo "windows\agent\FocusWindow.Agent\FocusWindow.Agent.csproj"

$cmakeCandidates = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
)
$cmake = $cmakeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cmake) {
    throw "Visual Studio 2022 CMake was not found. Install the Desktop C++ workload or pass a CMake path."
}

Write-Host "POC-BUILD configure $pocRoot"
& $cmake -S $pocRoot -B (Join-Path $pocRoot "build") -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE." }

Write-Host "POC-BUILD native $Configuration"
& $cmake --build (Join-Path $pocRoot "build") --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }

Write-Host "POC-BUILD agent $Configuration"
& dotnet build $agentProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Agent build failed with exit code $LASTEXITCODE." }

$nativeExe = Join-Path $pocRoot "build\bin\$Configuration\DeskFuse.GpuWorkspacePoc.exe"
$agentExe = Join-Path $repo "windows\agent\FocusWindow.Agent\bin\$Configuration\net8.0-windows\FocusWindow.Agent.exe"
if (-not (Test-Path $nativeExe)) { throw "Native executable missing: $nativeExe" }
if (-not (Test-Path $agentExe)) { throw "Agent executable missing: $agentExe" }

Write-Host "POC-BUILD OK"
Write-Host "  native=$nativeExe"
Write-Host "  agent=$agentExe"
