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
