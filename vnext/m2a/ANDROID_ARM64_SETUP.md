# DeskFuse vNext M2a — Android ARM64 native smoke test

**Current result (Windows 10 development machine, reported 2026-10-23):**
Native `android-arm64-smoke` cross-build **succeeded** using Rust 1.99.1,
cargo-ndk 4.1.2, NDK r30 (`30.0.16248370`), CMake/Ninja and
`quiche 0.30.0` / `boring-sys 5.3.0`.
The on-device ADB execution is **not yet confirmed**.
This is not an Android QUIC receiver or an APK.

The reference phone is Xiaomi model `24117RN76E`, Android API 36,
`arm64-v8a`, ADB-authorized. The build is for generic Android ARM64 and
does not depend on the phone brand. The separate working DeskFuse APK/driver
must not be modified.

## Installed tools

- Android SDK: `D:\Android\Sdk` (platform API 35)
- Android NDK: `D:\Android\Sdk\ndk\30.0.16248370`
- `rustup target add aarch64-linux-android`
- Rust Windows MSVC, `cargo-ndk`, Visual Studio 2022 developer PowerShell,
  CMake, Ninja, LLVM/libclang, NASM
- No Android emulator is needed: the physical phone is ADB-authorized.

If the NDK needs installation in a new environment:

```powershell
$sdk = 'D:\Android\Sdk'
$env:ANDROID_SDK_ROOT = $sdk
& "$sdk\cmdline-tools\latest\bin\sdkmanager.bat" "--sdk_root=$sdk" --install "ndk;30.0.16248370"
```

SDK Manager needs Java >=17. An SDK XML version warning is not an
installation failure if `ndk\30.0.16248370\source.properties` exists and
reports `Pkg.Revision = 30.0.16248370`.

## Reproducible Windows → Android ARM64 cross-build

A previously failed native CMake configuration can retain `CMakeSystem.cmake`
declaring `Windows` and `CMAKE_CROSSCOMPILING=FALSE`, even while its cache
mentions Android. Consequently its compiler checks try to link `kernel32`,
`user32` and `--major-image-version` using the Android Clang compiler.
Creating extensionless `clang` hard links does **not** solve this root cause.
The proper remedy is a target-specific Android NDK CMake toolchain.

Use the tracked wrapper:
[`quiche-lab/cmake/android-ndk-wrapper.cmake`](quiche-lab/cmake/android-ndk-wrapper.cmake).
It configures `arm64-v8a`, `android-35` and `c++_shared`, then includes
Google's NDK toolchain so the NDK selects the Windows host compiler executable.
This is the equivalent of the working local toolchain wrapper reported by the
developer; replacing an already-working local wrapper is unnecessary.

**On a new Visual Studio Developer PowerShell / Windows Terminal session:**

```powershell
cd 'D:\Coding\DeskFuse\vnext-wp001'
git pull --ff-only origin feature/vnext-wp001-native-lab
cd 'vnext\m2a\quiche-lab'

$env:ANDROID_SDK_ROOT = 'D:\Android\Sdk'
$env:ANDROID_NDK_HOME = 'D:\Android\Sdk\ndk\30.0.16248370'

# IMPORTANT: Build-script target-specific overrides, not global CMake settings.
$env:CMAKE_TOOLCHAIN_FILE_aarch64_linux_android = (
    Resolve-Path '.\cmake\android-ndk-wrapper.cmake'
).Path
$env:CMAKE_GENERATOR_aarch64_linux_android = 'Ninja'

# The HYPHENATED target suffix is necessary on cargo-ndk v4.1.2:
# cargo-ndk overwrites the underscore-suffixed bindgen setting.
Set-Item -Path 'Env:BINDGEN_EXTRA_CLANG_ARGS_aarch64-linux-android' `
    -Value '--target=aarch64-linux-android35'

# Command line: capital -P = Android API; lowercase -p = Cargo package.
cargo ndk -t arm64-v8a -P 35 build --bin android-arm64-smoke --locked
```

These variables apply to the current shell only. Reapply them in a new shell.
The initial successful report used a separate local wrapper with the same
five effective CMake lines, so the tracked wrapper is a source-controlled
reproduction rather than independently CI-verified cross-build.

**Only if the stale CMake target state causes a repeat failure**, clean
the affected native package once, not the entire project:

```powershell
cargo clean --package boring-sys --target aarch64-linux-android
cargo ndk -t arm64-v8a -P 35 build --bin android-arm64-smoke --locked
```

The project does not yet commit its local `Cargo.lock`; the developer's
`--locked` build succeeds because a lockfile exists in that checkout. Audit
and commit the lockfile to this isolated branch before promising reproducible
fresh-clone `--locked` builds.

## Execute the isolated native smoke test on the Android device

Only after the Android ARM64 build returns success, **from the quiche-lab
directory**:

```powershell
$exe = 'target\aarch64-linux-android\debug\android-arm64-smoke'
if (-not (Test-Path -LiteralPath $exe)) { throw "Missing Android binary: $exe" }
adb devices -l
adb push $exe /data/local/tmp/deskfuse-vnext-arm64-smoke
adb shell chmod 700 /data/local/tmp/deskfuse-vnext-arm64-smoke

# A standalone native executable does not have Gradle packaging its libc++.
# Push exactly the C++ runtime from the NDK that built this ARM64 binary.
$libcxx = "$env:ANDROID_NDK_HOME\toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\libc++_shared.so"
if (-not (Test-Path -LiteralPath $libcxx)) { throw "NDK C++ runtime missing: $libcxx" }
adb push $libcxx /data/local/tmp/libc++_shared.so

# Apply local runtime lookup for this invocation only; do not modify
# global Android loader paths or any installed DeskFuse APK.
adb shell 'LD_LIBRARY_PATH=/data/local/tmp /data/local/tmp/deskfuse-vnext-arm64-smoke'
```

Expected stdout:

```text
PASS DeskFuse vNext target=android arch=aarch64 real-quiche-config=true audio-frames=240 first-source-frame=48000 bytes=1000
NO NETWORKING: smoke test only; no QUIC peer authentication or Android playback.
```

If the device reports a missing `std::__ndk1::__libcpp_verbose_abort` symbol,\nthe matching NDK `libc++_shared.so` is probably not available to the\nstandalone executable. The above push and one-command `LD_LIBRARY_PATH`\nfix are necessary because there is no APK/Gradle step to package it.\nDo not replace any system libraries on the phone.\n\nThis only verifies the executable and linked quiche config run on the phone,
plus the PCM wire-format roundtrip. It is *not* proof of QUIC connectivity,
mutual endpoint authentication, time sync, Wi-Fi latency, or native audio.
It opens no UDP socket and installs no APK.

To remove the temporary test file (after stopping it):
`adb shell rm /data/local/tmp/deskfuse-vnext-arm64-smoke`.

## Storage and next acceptance gate

SDK/NDK and build outputs are on D:. Cargo registry/toolchain caches may be
under the Windows profile on C:. Avoid clearing working build artifacts or
modifying the existing C: Android Studio SDK.

Next: isolated authenticated Windows ↔ Android physical-device QUIC DATAGRAM
transport, source-frame identity and loss/freshness tracking; no Android
playback or production pairing claims until those gates are tested.
