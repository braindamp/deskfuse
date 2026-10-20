# DeskFuse v0.3 — START HERE

The current unified integration candidate is described in
[HANDOVER_UNIFIED_DESKFUSE.md](HANDOVER_UNIFIED_DESKFUSE.md) and
[docs/UNIFIED_RUNTIME.md](docs/UNIFIED_RUNTIME.md). It combines the production
WGC/D3D11/NVENC DLL, the Windows host, Device Bridge and the Android app in one
build. The historical snapshot instructions below are retained for provenance;
the unified candidate is software-tested, but is **not hardware accepted**.

**Source status:** `0.3.0-dev` / P2 hardened *candidate*. NOT a compiled release, NOT a hardware-verified phone connection, and NOT a new driver.

**Extract directly into:** `D:\Coding\DeskFuse\v0.3`

The extracted root should contain `windows/`, `android/`, `tools/`, `plans/` and this README.

**Read first:**

1. `DeskFuse_v0.3_PI_HANDOVER.md` — operational constraints and 48 required tests.
2. `DeskFuse_v0.3_RETURN_HANDSHAKE.md` — mandatory return evidence, acceptance gates and return filenames.
3. `plans/DeskFuse_Consolidated_Build_Plan_v0.3.md` — complete P0–P5 engineering roadmap.

**Canonical starter:** `DeskFuse_v0.3_FULL_SOURCE.zip`, do not overlay the historical P2 patch ZIP onto it.

**Application version:** Windows agent `--version` = `0.3.0-dev`; .NET project package versions = `0.3.0`; Android `versionName=0.3.0-dev`, `versionCode=3`. These are labels, **not evidence of compilation, driver approval, or stream operation**.

**PROTECTED:** historical installed FocusIdd v0.2.29 and verified development directory `D:\Coding\DeskFuse\DeskFuse_Phase_0_1_BuildFix_v0.2.1`.

Do not alter `windows/driver/**`, `windows/shared/**`, guarded driver install/rollback scripts, INF, the known-good installed driver, system/display topology or the protocol-v3 wire schema. Historical v0.2.29 proof scripts inside `tools/` are deliberately unchanged; **do not run them as a v0.3 launch or upgrade mechanism**. Pi must first determine whether the existing installed driver is available and safe to use.

## Where to start building (Windows PowerShell)

```powershell
Set-Location 'D:\Coding\DeskFuse\v0.3'
dotnet build '.\windows\agent\FocusWindow.Agent\FocusWindow.Agent.csproj' -c Release
dotnet test  '.\windows\agent\FocusWindow.Tests\FocusWindow.Tests.csproj' -c Release
Set-Location '.\android'
# Check whether gradlew.bat exists. The historical source snapshot did not ship a wrapper.
# Restore a compatible official wrapper or document an installed compatible Gradle.
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:testDebugUnitTest
```

**No tests were executed as part of the v0.3 metadata-only repackaging.** Pi must run and capture evidence, including real phone / authenticated LAN checks.

## Intended Pi return files

- `DeskFuse_v0.3_PI_FULL_SOURCE_RETURN.zip`
- `DeskFuse_v0.3_PI_SOURCE_PATCH.zip`
- `DeskFuse_v0.3_PI_EVIDENCE.zip`

The **48-test return handshake** decides acceptance; partial work must never be reported as 48/48.
