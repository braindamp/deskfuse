# DeskFuse libdatachannel WAN experiment

**Isolated connectivity spike, not production code.** No existing DeskFuse sources, tested LAN transport, Android app or signed Windows audio driver are changed.

This experiment tests a Windows ↔ Xiaomi Android network route with the libdatachannel C API, an ICE/STUN/TURN peer connection, a DTLS-protected DataChannel ping and selected ICE pair logging. It does **not** implement media, pairing, or production mobile handoff, and has not been run on the physical Xiaomi here.

## Security

The shared `DESKFUSE_PROBE_TOKEN` permits only two processes into a temporary test room. It authenticates **server access**, NOT the remote device's DTLS fingerprint end-to-end. A malicious signaling server could impersonate a participant. Do not send private audio, video, pairing keys, or other user data. Use disposable TURN credentials.

The server binds to localhost. For cross-internet tests, expose only `wss://` through a trusted HTTPS reverse proxy to localhost; native probe refuses insecure public `ws://`. Never commit tokens, and do not expose unsecured coturn to the internet.

## Run the rendezvous

```bash
python -m pip install -r experiments/wan-libdatachannel/requirements.txt
export DESKFUSE_PROBE_TOKEN="$(python -c 'import secrets; print(secrets.token_urlsafe(36))')"
python experiments/wan-libdatachannel/signaling.py --host 127.0.0.1 --port 8765
```

On Windows PowerShell, set `$env:DESKFUSE_PROBE_TOKEN` to the same random token. For cellular, use a trusted WSS proxy URL accessible by both endpoints.

## Build the client

Build and install **pinned libdatachannel v0.24.6** (or another reviewed revision) with WebSocket and DataChannel support first; its CMake package must be discoverable:

```bash
cmake -S experiments/wan-libdatachannel -B build/wan-probe -DCMAKE_PREFIX_PATH=/path/to/libdatachannel/install
cmake --build build/wan-probe --config Release
```

To run on the Xiaomi, cross-compile both libdatachannel and this standalone CLI for Android arm64 with the same NDK toolchain and API level. Use `adb push` and `adb shell` to execute the native binary; make its shared-library dependencies accessible. This ADB-shell experiment does **not** validate app-UID socket binding, Xiaomi power management or real Android application lifecycle.

## Two-party connectivity

```bash
# Windows / host
deskfuse-wan-probe --role host --room deskfuse123 --signal wss://probe.example.net --ice stun:stun.example.net:3478 --seconds 90
# Xiaomi / phone, with Wi-Fi disabled and data on
deskfuse-wan-probe --role phone --room deskfuse123 --signal wss://probe.example.net --ice stun:stun.example.net:3478 --seconds 90
```

Both peers require the same `DESKFUSE_PROBE_TOKEN` environment variable. Look for `PROBE_CONNECTED`, `PROBE_RTT_MS`, and `PROBE_RESULT=CONNECTED`. DataChannel ping RTT is **not** end-to-end audio/video latency.

Repeat with a coturn relay URI and `--relay-only` on both peers. Inspect the selected pair. Test TCP/TLS separately with the **compiled ICE backend**: default libjuice and optional libnice differ in TURN transport support. A TURN server supporting TLS does not prove a libjuice client can use it.

## Gate before media integration

Record hardware measurements on the actual Xiaomi for: direct/relay success, selected candidate pairs, setup time, p50/p95 ping RTT, reconnect and radio handoff behavior, battery effects and TURN/TLS blocking behavior. Only then consider WAN audio/video integration. libdatachannel provides packetization and SRTP, but NOT a complete media rate controller; congestion control remains unresolved.

Run signaling tests with `python -m unittest discover -s experiments/wan-libdatachannel/tests -v`. These tests exercise server access and forwarding, not libdatachannel binaries. Rollback means ignoring/removing this experiment directory; no production build references it.
