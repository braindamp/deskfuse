# DeskFuse Audio Lab v0.6 + v0.7 (integrated)

The Configuration Atlas (161 controls), tuning grid, notebook, local-file player, MM-1 listening reference and physical findings are preserved. Atlas drafts and exported plans never apply hardware settings. The localhost bridge is orchestration; all audio still uses the existing native pipeline.

## Configure

1. Build matching artifacts on `experiment/vnext-audio-latency` using `vnext/qualification/build-audio-pair.ps1 -IncludeProduct`. This builds/publishes the Release Windows host and native sources plus the development APK; it installs nothing. The receipt labels the APK **debug**, separately from the Release standalone artifacts. The development APK uses a Release Rust transport and its Gradle Debug C++ renderer; do not treat MM-1 CLI versus APK timing differences as a controlled capture-only experiment. The two product capture backends use the same APK.
2. Copy `config.example.json` to ignored `config.json`. Select explicit paths and the receipt. For product runs set `product_agent`, `windows_source_control`, `product_android_apk`, `product_android_package` and the existing Windows sender/capture paths. Agent dependencies must stay together in the published directory. For MM-1 retain the four existing artifact paths, local Lab keys and **explicit** physical endpoint ID.
3. Run `start-lab.ps1`. Preflight only probes broker reachability and verifies artifacts; it never provisions an endpoint. A matching already-installed APK and existing approved enrollment are prerequisites for product runs. APK/driver installation requires a separate authorized operation.

## Three real sources

- **MM-1 reference:** existing ADB qualification runner and unchanged listening settings. No default-device fallback. Supported Android one-factor comparisons remain available.
- **DeskFuse endpoint loopback:** existing product host, enrollment/registry identity, session certificate pins, broker-owned exact endpoint and WASAPI comparator.
- **Committed PCM:** the same product security and transport, using the existing direct Rust source. Live corrected-driver identity, export ABI 2, actual period of 1–240 frames and packet geometry (48k stereo PCM16, two notifications, packets 1–240 frames) must pass. Reference period can therefore fail; request minimum explicitly or use loopback. Never silently fall back.

Start is an explicit physical action. Product runs accept only one already-enrolled native speaker accessory, never display/input/file/microphone sessions or new enrollment. Connect that phone in speaker-only mode, then **manually** select its reported endpoint in the application. An occupied product-host port fails; the Lab does not kill another host or change routing. Source setup remains outside the QUIC owner. Shutdown joins capture and transport before releasing its lease; the runner also owns the host process tree via a kill-on-close job.

Minimum period is an executable **conditional request on the DeskFuse endpoint**, requiring exact running-driver capability and actual-period readback in native code. Receipt flags alone cannot authorize it. MM-1 minimum-engine requests remain invalid because MM-1 is not the DeskFuse driver. Direct PCM has no WASAPI allocation setting. Product source comparisons currently keep Android settings at the reference; unsupported combinations fail closed.

## Evidence

Each run records receipt hashes, source, requested policies and native observations under the configured local evidence root. Product runs independently hash the host dependency bundle and read back/hash the already-installed APK before starting the host; no credentials or manually supplied peer hashes reach the browser. Existing source timestamps, 60 ms stale rejection, generation and accepted-commit recovery fences are unchanged.

Compare recorded `LAB_ENDPOINT` IDs, **actual** period and format between dedicated runs before treating them as a same-endpoint comparison. A source selection or requested minimum is not measurement. Direct-source geometry/driver observations and Windows admission/drop counters are available; product Android callback/acoustic telemetry is not currently carried over the product control plane and remains explicitly unavailable. Queued PCM is not proof of audible output. No run is automatically called physically qualified; retain listening/acoustic observations separately.

Imported `CODEX_ULTRA_HANDOFF.md` and `V0_7_CAPTURE_ROADMAP_HANDOFF.md` are historical pre-2421b68 context, not current implementation restrictions. `LISTENING_TEST_REFERENCE.md` and `PHYSICAL_TEST_FINDINGS.md` remain the original reference evidence.

Focused checks: `python -m unittest discover -s tools/audio-lab/tests`; `python tools/audio-lab/sync_atlas.py` regenerates the Atlas browser data only.
