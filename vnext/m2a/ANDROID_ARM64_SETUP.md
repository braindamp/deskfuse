# M2a Android ARM64 build toolchain — Windows host

**Target device:** Xiaomi `24117RN76E`, Android API 36, `arm64-v8a` (confirmed over authorized ADB). **Build SDK:** existing `D:\Android\Sdk` with Android platform API 35. The build platform need not equal the device API. **Pinned NDK:** r30 LTS, `30.0.16248370` (official Android LTS download listing, October 2026). Use one checked toolchain version, not an implicit `latest`.

The Android NDK/rust target setup is a prerequisite only. There is **no Android receiver implementation or APK in this document**. Do not overwrite the currently working DeskFuse APK or install new virtual drivers.

## Install just the NDK onto D:

Use normal PowerShell (not elevated) with the already discovered SDK manager:

```powershell
$sdk = 'D:\Android\Sdk'
$ndkVersion = '30.0.16248370'
$env:ANDROID_SDK_ROOT = $sdk
& "$sdk\cmdline-tools\latest\bin\sdkmanager.bat" "--sdk_root=$sdk" --install "ndk;$ndkVersion"
```

Accept Google's license terms if prompted. This install can use several GB of transient/download/decompressed space on D:. It does not download an emulator or update the C: SDK. If Java is not found, install/point to an already trusted JDK instead of deleting or repointing either SDK.

## Verify the compiler

```powershell
$ndk = Join-Path $sdk "ndk\$ndkVersion"
Test-Path "$ndk\source.properties"
Get-Content "$ndk\source.properties" | Select-String 'Pkg.Revision'
& "$ndk\toolchains\llvm\prebuilt\windows-x86_64\bin\clang.exe" --version
& "$ndk\toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android35-clang.cmd" --version
rustup target add aarch64-linux-android
rustup target list --installed
adb shell getprop ro.product.cpu.abi
adb shell getprop ro.build.version.sdk
```

The `aarch64-linux-android35-clang.cmd` wrapper should exist in the NDK; 35 is the build API already installed and <= physical Android API 36. For future binaries, `minSdk` is chosen independently based on supported Android feature gates (for a 36-only lab, API 35 is fine). Do not infer permission, device or audio readiness from compiler checks.

## Storage policy

The SDK/NDK resides on D:. Windows profile Cargo registry, toolchain and Android emulator files may still consume C:. Monitor free space on both drives and keep a safe headroom. Do not move/delete the working C: Android SDK or emulator as part of this lab. The isolated Cargo `target` build outputs are inside the vNext worktree on D:.

## Next gates

Create a real Android arm64 Rust QUIC receiver in a separate artifact/package identity; use authenticated pairing/client authorization rather than accepting arbitrary peers. Establish a test control channel and real Windows↔Android DATAGRAMs over a documented route. Measure UDP receive clock, frame identities/epochs, queue bounds, loss/late accounting and full physical network timing before enabling any Android playback or replacing the existing app.


## Stage 1 — run the isolated ARM64 smoke executable on the device

This is **not** a QUIC network receiver yet. The standalone native binary lives in `quiche-lab/src/bin/android-arm64-smoke.rs`. It checks the source-indexed packet parser and instantiates an actual quiche crypto configuration on the Android CPU. It never installs an APK, opens UDP sockets or modifies the existing DeskFuse app.

Upstream quiche recommends `cargo-ndk` for Android cross-compilation; it configures the NDK linker, C/C++ dependencies and bindgen cross-target sysroot. On the Windows host, in **Visual Studio Developer PowerShell**:

```powershell
cd 'D:\Coding\DeskFuse\vnext-wp001'
git pull --ff-only origin feature/vnext-wp001-native-lab
rustup target add aarch64-linux-android

$env:ANDROID_SDK_ROOT = 'D:\Android\Sdk'
$env:ANDROID_NDK_HOME = 'D:\Android\Sdk\ndk\30.0.16248370'
if (-not (Test-Path "$env:ANDROID_NDK_HOME\source.properties")) {
    throw 'NDK r30 not installed on D:'
}

# cargo-ndk is a build-time tool, not an Android app or phone package.
cargo install cargo-ndk --locked

# API 35 is compatible with the tested Android API 36 phone.
# First cross-build compiles BoringSSL for ARM64 and may take longer.
cargo ndk -t arm64-v8a -p 35 -- build --manifest-path vnext/m2a/quiche-lab/Cargo.toml --bin android-arm64-smoke
```

**Only after the build succeeds**, run these direct USB ADB commands (single executable; no APK, no install):

```powershell
$exe = 'vnext\m2a\quiche-lab\target\aarch64-linux-android\debug\android-arm64-smoke'
if (-not (Test-Path $exe)) { throw 'ARM64 binary missing: build must pass first' }
adb devices -l
adb push $exe /data/local/tmp/deskfuse-vnext-arm64-smoke
adb shell chmod 700 /data/local/tmp/deskfuse-vnext-arm64-smoke
adb shell /data/local/tmp/deskfuse-vnext-arm64-smoke
```

Expected output includes `PASS DeskFuse vNext target=android arch=aarch64 real-quiche-config=true audio-frames=240 first-source-frame=48000 bytes=1000` followed by `NO NETWORKING`. If the binary fails, preserve the exact compiler/device error before trying different API levels or toolchains. Remove this isolated test file at any time using `adb shell rm /data/local/tmp/deskfuse-vnext-arm64-smoke`; do not uninstall the existing application.

Disk note: the `cargo-ndk` host tool and Rust std target use the profile's Cargo/Rust cache, typically on C:, while cross-compiled BoringSSL and executable outputs belong to the Cargo target folder on D:. Do not run `cargo clean` during builds. First build may be several GB; check C: and D: free space.

Passing this smoke check **does not** establish Windows-to-phone transport, QUIC peer identity, full connectivity, audio playback, clock synchronisation or multimedia quality. The next gate is explicitly authenticated physical-device QUIC.
