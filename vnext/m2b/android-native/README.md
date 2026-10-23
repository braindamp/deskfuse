# M2b Android native AAudio speaker — first audible vNext slice

This is an ISOLATED native renderer (Android API 26+), NOT an installed
DeskFuse APK change. AAudio uses the phone's default output route.

The Rust QUIC receiver authenticates and checks real Windows WASAPI PCM
before publication to the native C++ renderer. The native AAudio callback is
the sole consumer of a bounded 80 ms (16 x 240-frame) SPSC PCM16 stereo ring.
It requests audio independently of network arrival or transport ACKs.
Callback writes zeros on underrun, performs no allocation, sleeping, locks
or network I/O. The source may use an independent 48 kHz clock: final drift
correction is NOT implemented in this first audible prototype.

## Build from Visual Studio Developer PowerShell

    cd 'D:\Coding\DeskFuse\vnext-wp001'
    $env:ANDROID_NDK_HOME = 'D:\Android\Sdk\ndk\30.0.16248370'
    cmake -S vnext/m2b/android-native -B vnext/m2b/android-native/build-android -G Ninja "-DCMAKE_TOOLCHAIN_FILE=$env:ANDROID_NDK_HOME\build\cmake\android.toolchain.cmake" -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-35 -DANDROID_STL=c++_shared
    cmake --build vnext/m2b/android-native/build-android --parallel 2
    adb push vnext\m2b\android-native\build-android\libdeskfuse_android_audio.so /data/local/tmp/libdeskfuse_android_audio.so

Rebuild the Android Rust executable after fetching these source changes,
using the already working NDK r30 / cargo-ndk environment described in
vnext/m2a/ANDROID_ARM64_SETUP.md:

    cargo ndk -t arm64-v8a -P 35 build --bin continuous-quic-lab --locked
    adb push target\aarch64-linux-android\debug\continuous-quic-lab /data/local/tmp/deskfuse-vnext-continuous
    adb shell chmod 700 /data/local/tmp/deskfuse-vnext-continuous

Keep the existing matching NDK libc++_shared.so in /data/local/tmp and use
command-local LD_LIBRARY_PATH. Never replace Android system libraries.

## First audible session

On Windows (source), within vnext/m2a/quiche-lab in Developer PowerShell:

    $lab = 'D:\DeskFuseLocalKeys\m2a-physical-lab'
    $cap = (Resolve-Path '..\..\m1\build\deskfuse-wasapi-capture.exe').Path
    $device = '{0.0.0.00000000}.{2ae075dc-0b84-43cf-bade-f86449a5f6fa}'
    .\target\debug\continuous-quic-lab.exe server-wasapi 192.168.2.107:48235 192.168.2.114 "$lab\ca.pem" "$lab\server.pem" "$lab\server.key" "$cap" "$device" 10

On Xiaomi (sink), after Windows prints READY:

    adb shell 'LD_LIBRARY_PATH=/data/local/tmp /data/local/tmp/deskfuse-vnext-continuous client-playback 192.168.2.107:48235 /data/local/tmp/df-lab-ca.pem /data/local/tmp/df-lab-client.pem /data/local/tmp/df-lab-client.key /data/local/tmp/libdeskfuse_android_audio.so 10'

Have Windows output playing through Speakers (MM-1) during the run.
MM-1 is the temporary CAPTURE endpoint. The intended output is the Xiaomi
phone speaker. Do not change existing DeskFuse driver or Windows routing.

Expected: AUTH at both peers; AUDIO Android native AAudio output started;
later rendered_frames > 0. Nonzero output frames alone do not establish
audibility; verify actual audible music and phone output route/volume.

## First physical audible playback — developer observation (2026-10-23)

**PASS: real Windows audio was heard from the Xiaomi handset over authenticated
QUIC, using Windows WASAPI capture and the native Android AAudio sink.**
Audio otherwise sounded clean; a small crackle/pop was heard during volume
adjustments. This is developer-reported physical hardware evidence, not a
latency, stress, or production-quality acceptance claim.

For the 10-second capture / receiver observation:
- Windows WASAPI: 480,480 frames acquired; bounded writer drops 0.
- Windows: 1,958 PCM DATAGRAMs queued; QUIC DATAGRAM queue full 44 times.
- Android: 1,957 PCM DATAGRAMs received; 45 missing sequence positions
  (= 10,800 source frames). One offered DATAGRAM was not received.
- Android AAudio: 469,680 PCM source frames accepted and rendered; 89,040
  zero-output frames (including session boundary priming/drain), 0 native
  ring overflows, 0 renderer errors. No measured acoustic latency yet.

The source packet counters separate the 44 sender-side queue misses from
the one additional missing receiver packet. The volume-change pop is not
yet localized to capture gain, network gap, or Android output gain. The
next implementation iterations prioritize timely QUIC service during
bursty capture and click-free AAudio start/underrun behavior, followed by
source/sink drift adaptation and Android app lifecycle integration.

## Scope and limits

The C++ native ARM64 library build was verified in GitHub CI. Real hardware
audibility, sustained Wi-Fi, acoustic latency, drift control, generation
handling, underrun recovery and Android lifecycle/audio focus are NOT
qualified. An AAudio failure from an ADB shell process may require an
isolated app-host integration; do not bypass Android security or patch the
existing installed APK. No credentials, private keys, recorded audio or
phone identifiers should be committed to the public repo.
