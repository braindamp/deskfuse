# Build the exact CP-001/MP-001 Windows sender and ABI v5 Android pair.
# No ADB deployment, credentials, driver installs or existing APK changes.
[CmdletBinding()]
param(
    [string]$LabRoot = 'D:\Coding\DeskFuse\vnext-wp001',
    [string]$AndroidSdk = 'D:\Android\Sdk',
    [string]$AndroidNdk = 'D:\Android\Sdk\ndk\30.0.16248370'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LabRoot -PathType Container)) {
    throw 'Isolated DeskFuse vNext checkout missing'
}
$branch = (& git -C $LabRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'feature/vnext-wp001-native-lab') {
    throw 'Refusing to build from main, preserved reference or unknown branch'
}
$dirty = @(& git -C $LabRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) {
    throw 'Commit or intentionally isolate local modifications before building evidentiary binaries'
}
if (-not (Test-Path -LiteralPath (Join-Path $AndroidNdk 'build\cmake\android.toolchain.cmake'))) {
    throw 'Pinned Android NDK r30 missing'
}
$env:ANDROID_HOME = $AndroidSdk
$env:ANDROID_SDK_ROOT = $AndroidSdk
$env:ANDROID_NDK_HOME = $AndroidNdk
$nativeSource = Join-Path $LabRoot 'vnext\m2b\android-native'
$nativeBuild = Join-Path $nativeSource 'build-android'
$toolchain = Join-Path $AndroidNdk 'build\cmake\android.toolchain.cmake'
$configure = @('-S',$nativeSource,'-B',$nativeBuild,'-G','Ninja',
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    '-DANDROID_ABI=arm64-v8a','-DANDROID_PLATFORM=android-35',
    '-DANDROID_STL=c++_shared')
& cmake @configure
if ($LASTEXITCODE -ne 0) { throw 'Android native audio configure failed' }
& cmake --build $nativeBuild --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Android native audio compile failed' }

$crate = Join-Path $LabRoot 'vnext\m2a\quiche-lab'
$wrapper = Join-Path $crate 'cmake\android-ndk-wrapper.cmake'
if (-not (Test-Path -LiteralPath $wrapper -PathType Leaf)) {
    throw 'Pinned Android Rust CMake cross-target wrapper missing'
}
$env:CMAKE_TOOLCHAIN_FILE_aarch64_linux_android = (Resolve-Path -LiteralPath $wrapper).Path
$env:CMAKE_GENERATOR_aarch64_linux_android = 'Ninja'
Set-Item -Path 'Env:BINDGEN_EXTRA_CLANG_ARGS_aarch64-linux-android' -Value '--target=aarch64-linux-android35'

Push-Location -LiteralPath $crate
try {
    & cargo build --locked --bin continuous-quic-lab
    if ($LASTEXITCODE -ne 0) { throw 'Windows Rust source build failed' }
    & cargo ndk -t arm64-v8a -P 35 build --bin continuous-quic-lab --locked
    if ($LASTEXITCODE -ne 0) { throw 'Android ARM64 Rust transport build failed' }
}
finally { Pop-Location }

$files = @(
    (Join-Path $crate 'target\debug\continuous-quic-lab.exe'),
    (Join-Path $crate 'target\aarch64-linux-android\debug\continuous-quic-lab'),
    (Join-Path $nativeBuild 'libdeskfuse_android_audio.so'),
    (Join-Path $LabRoot 'vnext\m1\build\deskfuse-wasapi-capture.exe')
)
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw ('Expected local output missing: ' + $file)
    }
}
$sha = (& git -C $LabRoot rev-parse HEAD).Trim()
Write-Host ('CP-001/MP-001 source commit: ' + $sha)
foreach ($file in $files) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ($hash + '  ' + $file)
}
Write-Host 'Built locally. No device or driver was changed.'
