# M2a Physical QUIC lab: Windows source -> Android sink

Status: **physical Windows-to-Xiaomi Wi-Fi QUIC connectivity PASS** on 2026-10-23,
reported from both endpoint terminals; **M2a overall is still IN PROGRESS**.
Android INITIATES QUIC, but Windows SENDS 16 indexed PCM DATAGRAMs.
Android verifies and echoes them. No APK, driver or real audio device is touched.
Temporary certificates and private keys remain OUTSIDE public GitHub.

## 2026-10-23 physical hardware evidence (developer-reported)

Tested source HEAD: `9b62a1d003849b382b4a77c504caf2fbc871c0c6`,
isolated feature branch `feature/vnext-wp001-native-lab`.
Windows 10 Ethernet source/listener to Xiaomi Android ARM64 Wi-Fi initiator/sink,
on the same private LAN. Local disposable CA, server and client identity files
were generated outside the Git checkout. **Do not commit keys, live certificates,
pairing secrets, local test key directories, or machine-specific IPs.**

- **Positive mutual-TLS and physical UDP test: PASS.** Android verified the
  Windows server's CA chain and `deskfuse-lab.local` identity. Windows verified
  the Android client's CA-issued TLS certificate was presented.
- **Source-indexed payload integrity: PASS.** Android verified all 16 Windows-origin
  1,000-byte app DATAGRAMs: packet sequences 0–15, source frames 48000–51600
  at 240-frame intervals, with exact expected payloads. Windows verified all
  16 encrypted echoes. User-observed mean *sequential application QUIC roundtrip*
  was **7.42 ms**; not a one-way network RTT, streaming throughput, acoustic
  latency, or A/V synchronization result.
- **Untrusted Android client CA rejection: PASS.** Windows with its original
  trusted CA reported `server QUIC recv failed: TlsFail` when the client
  presented an independently issued cert; Android reported that the connection
  closed before exchange completed. Android's server-authentication message
  in that run did not authorize the client.
- **Untrusted Windows server CA rejection: PASS.** With Android explicitly
  configured to trust an unrelated CA while using its original client identity,
  Android reported `client recv failed: TlsFail`; Windows reported the
  connection closed before exchange completed. No 16-packet exchange succeeded.

**Evidence scope:** observations above are from developer-supplied terminal logs
on physical hardware, not an independent device run or a reproducible CI archive.
The CLI fixture is stop-and-wait (16 packets), uses 2 ms sleeps, and is not a
continuous event-driven media transport. Remaining security cases include missing
client certificate, wrong server name, wrong certificate role, and identity-specific
authorization. M2a release-time ownership, sealed-packet `WouldBlock` retention,
clock freshness, bounded reactor, recovery and sustained-load acceptance are **not
yet qualified**; retain the thresholds in `PREREGISTERED_GATES.md`.
The older `udp_loopback` test failed on Intel macOS in CI run
[39902315232](https://github.com/braindamp/deskfuse/actions/runs/39902315232);
do not claim the four-platform suite is green.


## 1. Windows test binary and disposable local credentials

Run in Visual Studio 2022 Developer PowerShell:

    cd 'D:\Coding\DeskFuse\vnext-wp001'
    git pull --ff-only origin feature/vnext-wp001-native-lab
    cd 'vnext\m2a\quiche-lab'
    cargo build --locked --bin physical-quic-lab

    $lab = 'D:\DeskFuseLocalKeys\m2a-physical-lab'
    New-Item -ItemType Directory -Path (Split-Path $lab) -Force | Out-Null
    $env:DESKFUSE_LAB_CERT_DIR = $lab
    cargo test --locked --test issue_physical_lab_identity issue_local_physical_lab_identity -- --ignored --nocapture
    Get-ChildItem $lab | Select-Object Name, Length

Creates ca.pem, server.pem, server.key, client.pem and client.key.
Do not commit or share these. This fixture refuses an existing/nonempty
destination to avoid silently overwriting old credentials.

## 2. Find real LAN IPs; Wi-Fi needs to be reachable

    Get-NetIPAddress -AddressFamily IPv4 |
      Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
      Select-Object InterfaceAlias, IPAddress
    adb shell ip -4 addr show wlan0
    adb shell ip route

Choose the real Windows LAN IP and Android wlan0 IP. USB ADB does not
forward UDP; both devices must have a LAN route to each other.

## 3. Android binary and test credentials

Use the existing working Android NDK r30 wrapper settings, from quiche-lab:

    $env:ANDROID_SDK_ROOT = 'D:\Android\Sdk'
    $env:ANDROID_NDK_HOME = 'D:\Android\Sdk\ndk\30.0.16248370'
    $env:CMAKE_TOOLCHAIN_FILE_aarch64_linux_android = (Resolve-Path '.\cmake\android-ndk-wrapper.cmake').Path
    $env:CMAKE_GENERATOR_aarch64_linux_android = 'Ninja'
    Set-Item -Path 'Env:BINDGEN_EXTRA_CLANG_ARGS_aarch64-linux-android' -Value '--target=aarch64-linux-android35'

    cargo ndk -t arm64-v8a -P 35 build --bin physical-quic-lab --locked

    $phoneExe = 'target\aarch64-linux-android\debug\physical-quic-lab'
    if (-not (Test-Path $phoneExe)) { throw 'Android binary missing' }
    adb push $phoneExe /data/local/tmp/deskfuse-vnext-physical
    adb shell chmod 700 /data/local/tmp/deskfuse-vnext-physical

    $libcxx = "$env:ANDROID_NDK_HOME\toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\libc++_shared.so"
    if (-not (Test-Path $libcxx)) { throw 'NDK C++ runtime missing' }
    adb push $libcxx /data/local/tmp/libc++_shared.so

    adb push "$lab\ca.pem" /data/local/tmp/df-lab-ca.pem
    adb push "$lab\client.pem" /data/local/tmp/df-lab-client.pem
    adb push "$lab\client.key" /data/local/tmp/df-lab-client.key
    adb shell chmod 600 /data/local/tmp/df-lab-client.key

The server key remains on Windows; the phone's disposable client key
exists only in the ADB-shell private test location while testing.

## 4. Start Windows listener, then Android

WINDOW A in quiche-lab (replace both placeholders using step 2):

    $lab = 'D:\DeskFuseLocalKeys\m2a-physical-lab'
    $windowsIp = 'REPLACE_WITH_WINDOWS_LAN_IP'
    $phoneIp = 'REPLACE_WITH_ANDROID_WIFI_IP'
    $windowsAddress = $windowsIp + ':48235'
    .\target\debug\physical-quic-lab.exe server $windowsAddress $phoneIp "$lab\ca.pem" "$lab\server.pem" "$lab\server.key"

Wait for READY Windows UDP listener. In WINDOW B:

    $windowsIp = 'REPLACE_WITH_WINDOWS_LAN_IP'
    $windowsAddress = $windowsIp + ':48235'
    adb shell "LD_LIBRARY_PATH=/data/local/tmp /data/local/tmp/deskfuse-vnext-physical client $windowsAddress /data/local/tmp/df-lab-ca.pem /data/local/tmp/df-lab-client.pem /data/local/tmp/df-lab-client.key"

Windows expects certificate verification and 16 returned QUIC echoes.
Android expects server verification and 16 indexed PCM packets.
Transport is stop-and-wait for the fixture only: this is not a measured
audio pipeline. On timeout verify IPs, Wi-Fi reachability and Windows
Firewall. If needed, scope an inbound UDP 48235 firewall rule to the
specific phone IP on a private network; never disable the firewall.

## 5. Cleanup

    adb shell rm /data/local/tmp/deskfuse-vnext-physical
    adb shell rm /data/local/tmp/df-lab-ca.pem /data/local/tmp/df-lab-client.pem /data/local/tmp/df-lab-client.key

The disposable credential directory under D:\DeskFuseLocalKeys can be
deleted after the test. Do not change the installed DeskFuse application.

Initiator and media source are independent roles. Future DeskFuse
Android<->iPhone and Linux<->phone peers need no Windows middleman.
Production pairing, network traversal and streaming latency are later gates.
