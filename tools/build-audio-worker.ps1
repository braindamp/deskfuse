# Build/stage the fixed native role. This neither approves nor installs a closure.
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$taskPackage = Join-Path $taskRoot 'vnext\m2a\quiche-lab'
$taskCargoTarget = Join-Path $taskPackage 'target'
$taskTarget = 'x86_64-pc-windows-msvc'
$taskBuild = Join-Path $taskRoot 'out\audio-worker\source-control'
$taskPayload = Join-Path $taskRoot 'out\audio-worker\payload'
$taskProfile = $Configuration.ToLowerInvariant()
if ($taskProfile -eq 'debug') { $taskCargoProfile = 'dev' } else { $taskCargoProfile = 'release' }
function Assert-WorkerX64Image([string]$Path) {
    $taskImageStream = [System.IO.File]::OpenRead($Path)
    $taskImageReader = [System.IO.BinaryReader]::new($taskImageStream)
    try {
        if ($taskImageReader.ReadUInt16() -ne 0x5a4d) { throw 'Native artifact is not a PE image.' }
        $taskImageStream.Position = 0x3c
        $taskPeOffset = $taskImageReader.ReadInt32()
        if ($taskPeOffset -lt 64 -or $taskPeOffset -gt $taskImageStream.Length - 6) { throw 'Invalid native PE header.' }
        $taskImageStream.Position = $taskPeOffset
        if ($taskImageReader.ReadUInt32() -ne 0x4550 -or $taskImageReader.ReadUInt16() -ne 0x8664) {
            throw 'The fixed worker and native dependency must both be x64 PE images.'
        }
    }
    finally { $taskImageReader.Dispose(); $taskImageStream.Dispose() }
}
foreach ($taskCommand in @('cargo.exe', 'cmake.exe', 'ninja.exe', 'cl.exe', 'nasm.exe')) {
    if (-not (Get-Command $taskCommand -ErrorAction SilentlyContinue)) { throw "$taskCommand is required in the x64 developer shell." }
}
& cargo build --manifest-path (Join-Path $taskPackage 'Cargo.toml') --locked --offline --target $taskTarget --target-dir $taskCargoTarget --profile $taskCargoProfile --bin deskfuse-audio-worker
if ($LASTEXITCODE -ne 0) { throw 'Fixed native worker build failed.' }
& cmake -S (Join-Path $taskRoot 'vnext\m1') -B $taskBuild -G Ninja "-DCMAKE_BUILD_TYPE=$Configuration" -DBUILD_TESTING=OFF
if ($LASTEXITCODE -ne 0) { throw 'Native source-control configuration failed.' }
& cmake --build $taskBuild --target deskfuse-source-control --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Native source-control build failed.' }
New-Item -ItemType Directory -Force -Path $taskPayload | Out-Null
# Cargo names cannot contain a dot. The installed entrypoint remains exactly
# DeskFuse.AudioWorker.exe; only this build step maps the Cargo target's name.
$taskWorker = Join-Path $taskPayload 'DeskFuse.AudioWorker.exe'
$taskDll = Join-Path $taskPayload 'deskfuse-source-control.dll'
$taskBuiltWorker = Join-Path $taskCargoTarget "$taskTarget\$taskProfile\deskfuse-audio-worker.exe"
$taskBuiltDll = Join-Path $taskBuild 'deskfuse-source-control.dll'
Assert-WorkerX64Image $taskBuiltWorker
Assert-WorkerX64Image $taskBuiltDll
Copy-Item -LiteralPath $taskBuiltWorker -Destination $taskWorker
Copy-Item -LiteralPath $taskBuiltDll -Destination $taskDll
Get-FileHash -LiteralPath $taskWorker, $taskDll -Algorithm SHA256 | Select-Object Path, Hash
