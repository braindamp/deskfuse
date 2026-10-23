# DeskFuse vNext — WP-001 native timing/ownership laboratory

**Isolated experimental C++20 test project. Not the installed DeskFuse application.**

This path is intentionally separate from Windows audio drivers, the C# agent, Android APK and video pipeline. The branch is for validating ownership, source-frame identity, deterministic fault handling and two-thread SPSC behavior before the Windows/Android QUIC milestone.

## Windows build (Visual Studio 2022 Developer PowerShell)

From the repository worktree root:

```powershell
cmake -S vnext/wp001 -B vnext/wp001/build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build vnext/wp001/build --parallel 2
ctest --test-dir vnext/wp001/build --output-on-failure
.\vnext\wp001\build\df_timing_lab.exe burst100
.\vnext\wp001\build\df_timing_lab.exe stall100
.\vnext\wp001\build\df_timing_lab.exe idle_reset
```

**The complete source files must be present before these commands can build.** This README is an initial repository landing point. The C++ source is to be committed separately, without any downloaded PowerShell scripts.

Never execute old ZIP-bundled PowerShell scripts or disable Microsoft Defender to run this experiment. Use a separate Git worktree; don't switch the working DeskFuse checkout away from its current branch.

## Acceptance policy

- Eight fixed SPSC slots; only the consumer advances/releases the tail.
- Frames are identified by generation and source index, never by arrival time.
- No retroactive rewrite of played or committed intervals.
- Source idle means **no new frames**; restart establishes a new generation.
- Feedback credit publication time and lease are checked; late delivery does not renew a lease.
- Actual producer/consumer threads tested in addition to deterministic interleavings.
- No driver install, audio-route change, network traffic or Android APK modification.

**Test status:** Linux WP-001 standalone validation completed previously; Windows native build and source publication into this branch are pending. Do not claim Windows success before its CTest results are collected.
