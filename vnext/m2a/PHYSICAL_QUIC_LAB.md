# M2a Physical QUIC lab: Windows source -> Android sink

Status: isolated test source committed, physical Wi-Fi test not yet passed.
Android INITIATES QUIC, but Windows SENDS 16 indexed PCM DATAGRAMs.
Android verifies and echoes them. No APK, driver or real audio device is touched.
Temporary certificates and private keys remain OUTSIDE public GitHub.

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
