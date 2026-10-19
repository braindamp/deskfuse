# DeskFuse — new-session starting prompt (20 October 2026)

**Authoritative handover:** [HANDOVER_START_HERE.md](HANDOVER_START_HERE.md)  
**Product direction:** [docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md](docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md)  
**Consolidated build plan:** [plans/DeskFuse_Consolidated_Build_Plan_v0.3.md](https://github.com/braindamp/deskfuse/blob/main/plans/DeskFuse_Consolidated_Build_Plan_v0.3.md)

Copy this prompt into a fresh DeskFuse development chat:

> You are continuing **DeskFuse** (https://github.com/braindamp/deskfuse), the full Windows↔Android phone-first productivity and window-streaming product—not merely its scaling lab. Read `HANDOVER_START_HERE.md` and `docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md` on documentation branch `docs/window-first-architecture-20261019`, then the current P0–P5 consolidated plan on `main`.
>
> **Status:** the earlier Phase 0–1 hardware-check campaign and v0.3 return-handshake test execution have been **concluded** per the owner; inspect actual returned results before claiming specific gates passed, but do not re-run the entire campaign as the default task. The unresolved acceptance requirement is **true 350% / 336-DPI Windows rendering at the exact 1440×3120 virtual monitor**. Historical unsupported DPI writes stored 350% but independent native PMv2 Windows apps still saw 225%; don't confuse readback with applied pixels.
>
> **Parallel work:** a different agent is **currently implementing** the `poc/gpu-phone-workspace-wgc` experiment: 2100×3120 virtual monitor @ 350% → native 1440×3120 HWND → WGC/D3D11 capture and encoder, with stable per-client display identities and reconnection DPI validation. **Do not take over, widen, merge or retest its assignment** unless the owner later hands back its results.
>
> **Your focus now:** continue the broader DeskFuse architecture and roadmap for a **window-first spatial 2D/2.5D desk** with topic-organized window cards; phone-native zoom/focus into one activity, a backend chosen per source (native HWND+WGC, managed-browser CDP/offscreen/GPU research, separate explicit full desktop), selective previews and correct input mapping. Preserve the complete competitive feature list, performance, security and Android workflow. Keep real Windows touch (`InjectSyntheticPointerInput`), mouse/keyboard/IME, gamepad alternatives (ViGEm is retired), and a privileged-broker + least-privileged user-session agent as **future design research**. SYSTEM alone does not confer access to UAC/secure desktop/lock screen/Ctrl+Alt+Del.
>
> Work only in a **separate branch/worktree**; `main` is shared, and the installed FocusIdd driver is shared across worktrees. Do not reinstall/alter the driver or touch physical monitors. Propose the most valuable *non-POC* next development milestone with acceptance tests and distinguish implemented functionality from ideas and blocked platform behavior.

**Do not treat this starting prompt as new implementation authorization for privileged services, OS input drivers or system display changes.**
