# Build the matching Windows sender and ABI v8 Android pair.
# No ADB deployment, credentials, driver installs or existing APK changes.
[CmdletBinding()]
param(
    [string]$LabRoot = 'D:\Coding\DeskFuse\vnext-wp001',
    [string]$AndroidSdk = 'D:\Android\Sdk',
    [string]$AndroidNdk = 'D:\Android\Sdk\ndk\30.0.16248370',
    [string]$ExpectedBranch = 'feature/vnext-wp001-native-lab',
    [ValidateSet('release','debug')][string]$RustProfile = 'release'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LabRoot -PathType Container)) {
    throw 'Isolated DeskFuse vNext checkout missing'
}
$branch = (& git -c ("safe.directory=" + $LabRoot) -C $LabRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne $ExpectedBranch -or
    $branch -in @('main', 'master') -or $branch.StartsWith('reference/')) {
    throw 'Refusing to build from main, preserved reference or unknown branch'
}
$dirty = @(& git -c ("safe.directory=" + $LabRoot) -C $LabRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect local Git worktree' }
if ($dirty.Count -gt 0) {
    Write-Warning ('Building with {0} local Git worktree change(s). No files will be deleted, stashed, or reset. Binary SHA256 hashes will still be printed; this is not clean-commit qualification evidence.' -f $dirty.Count)
}
if (-not (Test-Path -LiteralPath (Join-Path $AndroidNdk 'build\cmake\android.toolchain.cmake'))) {
    throw 'Pinned Android NDK r30 missing'
}
$env:ANDROID_HOME = $AndroidSdk
$env:ANDROID_SDK_ROOT = $AndroidSdk
$env:ANDROID_NDK_HOME = $AndroidNdk
$wasapiSource = Join-Path $LabRoot 'vnext\m1'
$wasapiBuild = Join-Path $wasapiSource 'build'
# Ninja is single-config: --config Release does not override a Debug cache.
# Reconfigure the existing isolated build tree so reported binary is Release.
& cmake -S $wasapiSource -B $wasapiBuild -G Ninja -DCMAKE_BUILD_TYPE=Release
if ($LASTEXITCODE -ne 0) { throw 'Native Windows capture Release configure failed' }
& cmake --build $wasapiBuild --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Native WASAPI capture build failed' }

$nativeSource = Join-Path $LabRoot 'vnext\m2b\android-native'
$nativeBuild = Join-Path $nativeSource 'build-android'
$toolchain = Join-Path $AndroidNdk 'build\cmake\android.toolchain.cmake'
$configure = @('-S',$nativeSource,'-B',$nativeBuild,'-G','Ninja',
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    '-DANDROID_ABI=arm64-v8a','-DANDROID_PLATFORM=android-35',
    '-DANDROID_STL=c++_shared','-DCMAKE_BUILD_TYPE=Release')
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
    # Cargo resolves CARGO_TARGET_DIR (including relative paths) and any
    # configured target-dir from this exact crate working directory.
    $metadataText = @(& cargo metadata --locked --no-deps --format-version 1)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve Cargo target directory' }
    $cargoTarget = ($metadataText -join [Environment]::NewLine | ConvertFrom-Json).target_directory
    if ([string]::IsNullOrWhiteSpace($cargoTarget)) { throw 'Cargo returned no target directory' }
    $profileArgs = @()
    if ($RustProfile -eq 'release') { $profileArgs = @('--release') }
    & cargo build --locked --bin continuous-quic-lab --target-dir $cargoTarget @profileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Windows Rust source build failed' }
    & cargo ndk -t arm64-v8a -P 35 build --bin continuous-quic-lab --locked --target-dir $cargoTarget @profileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Android ARM64 Rust transport build failed' }
}
finally { Pop-Location }

$files = @(
    (Join-Path $cargoTarget ($RustProfile + '\continuous-quic-lab.exe')),
    (Join-Path $cargoTarget ('aarch64-linux-android\' + $RustProfile + '\continuous-quic-lab')),
    (Join-Path $nativeBuild 'libdeskfuse_android_audio.so'),
    (Join-Path $LabRoot 'vnext\m1\build\deskfuse-wasapi-capture.exe')
)
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw ('Expected local output missing: ' + $file)
    }
}
$sha = (& git -c ("safe.directory=" + $LabRoot) -C $LabRoot rev-parse HEAD).Trim()
foreach ($tree in @($wasapiBuild, $nativeBuild)) {
    $cache = Join-Path $tree 'CMakeCache.txt'
    $buildType = @(Select-String -LiteralPath $cache -Pattern '^CMAKE_BUILD_TYPE:STRING=Release$')
    if ($buildType.Count -ne 1) {
        throw ('Expected single-config Release native build: ' + $cache)
    }
    Write-Host ('Native CMake configuration: Release (Ninja) ' + $tree)
}
Write-Host ('Rust Cargo configuration: ' + $RustProfile + ' (both Windows and Android; --locked).')
Write-Host ('Cargo target directory: ' + $cargoTarget)
Write-Host ('CP-001/MP-001 source commit: ' + $sha)
$receiptArtifacts = @()
for ($i = 0; $i -lt $files.Count; ++$i) {
    $file = $files[$i]
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ($hash + '  ' + $file)
    $profile = if ($i -lt 2) { $RustProfile } else { 'release' }
    $role = @('windows_rust','android_rust','android_native','windows_capture')[$i]
    $receiptArtifacts += [pscustomobject]@{role=$role; path=(Resolve-Path -LiteralPath $file).Path; sha256=$hash; profile=$profile}
}
$receiptPath = Join-Path $wasapiBuild 'audio-pair-build.json'
[pscustomobject]@{
    schema=1; source_commit=$sha; built_utc=(Get-Date).ToUniversalTime().ToString('o')
    capabilities=@{android_audio_abi=8; windows_capture_policies=@('reference','minimum'); native_reserve_frames=@(1440,1200,960,720,480); callback_measurement=$true}
    cargo_target_directory=$cargoTarget; artifacts=$receiptArtifacts
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
Write-Host ('Hash-bound local build receipt: ' + $receiptPath)
Write-Host 'Built locally. No device or driver was changed.'
