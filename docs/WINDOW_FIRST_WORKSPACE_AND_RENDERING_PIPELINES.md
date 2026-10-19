# DeskFuse — Window-first workspace and adaptive rendering pipeline

**Document ID:** DF-ARCH-WINDOW-FIRST-001  
**Status:** Product direction and research backlog; **not** a validated implementation or a change in Approach A acceptance  
**Date:** 2026-10-19  
**Related POC:** [`DF-GPU-WGC-POC-001` v1.2](./DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md) (standalone WGC/GPU window-capture experiment)  
**Repository:** https://github.com/braindamp/deskfuse

## 1. Product intent (from the DeskFuse design conversation)

**DeskFuse is not primarily a smaller Windows desktop.** Its default experience should be an organized collection of *individual windows and activities* accessible from a phone. The normal interaction is:

1. Browse a phone-native **virtual desk space**, where live app/window representations are grouped around projects/topics.
2. Tap/zoom into one selected window. DeskFuse chooses the most appropriate backend, renders/captures the selected content at the **phone's actual pixel size and appropriate DPR**, and presents it at low latency with correct touch, keyboard, scroll, focus and clipboard handling.
3. Zoom back out to the desk, drag/arrange windows into groups, switch between them, and preserve the workspace organization.
4. Offer **full Windows desktop streaming as a distinct optional source/pipeline**, when required for games, unsupported apps, multiwindow OS workflows, troubleshooting, etc.

A 2D or **2.5D** spatial canvas (zoom, pan, nesting, depth-like grouping) is the initial concept; literal 3D rendering is **not** a requirement until usability and performance evidence justify it. This is a **future product experience**, not permission to expand the current WGC POC.

The full DeskFuse feature vision remains important: low-latency remote input, native-feeling text, app switching, audio/video calls, gaming/compatibility, file/clipboard workflows, secure pairing, resilient reconnections and phone-first UX. No DPI/capture POC should be represented as completing that broader product.

## 2. Proposed source and rendering model

```text
Windows window inventory + managed browser content + optional full desktop
                         |
        WindowSource catalog / stable logical source identity
                         |
                  Desk workspace model
          topics | cards | grouping | z-order | focus
                         |
                RenderPipeline selector
            /                  |                  \
   Windows native HWND     Managed browser       Full desktop
      PMv2 + WGC         CDP / OSR / CEF        FocusIdd/DXGI/WGC
      D3D11 GPU           GPU texture if viable    separate mode
            \                  |                  /
                  frames + timestamps + input map
                         |
               selective streaming policy
          focused source: crisp, low latency / high FPS
          unfocused sources: low-rate thumbnails / pause
                         |
                  Android phone-native desk
```

These are **logical interfaces**, not a claim that any particular backend is already built. Avoid defining the whole desktop as the root abstraction. A `WindowSource` may be a native HWND, an app-defined managed browser surface, a virtual document view or a full desktop, each with a distinct capture and input contract.

A focused source is **not always the same object as its original HWND**; for browser-controlled sources it may be a new off-screen browser rendering surface. Product design must address session/cookie continuity, website permissions and preserving existing tabs without silently moving users into a different browser profile.

## 3. Rendering backend decision table — research priorities

| Source type | Candidate pipeline | DPI/layout control | Limitation / proof required |
| --- | --- | --- | --- |
| Arbitrary native Windows application | High-DPI FocusIdd monitor -> native HWND -> **Windows.Graphics.Capture** -> D3D11 -> hardware encode | Windows PMv2 monitor DPI; independently verify 336 DPI/350-pixel 100-DIP ruler | Supported 2100x3120 at 350% may be needed; HWND-only capture may omit dialogs/popups; minimized and protected content limitations |
| Existing user Chrome/Edge/Electron/other HWND | **WGC window capture** as general fallback | Usually inherits existing app/OS DPI; cannot assume controlled CSS devicePixelRatio | Do not assume DevTools is available on an already-running browser or silently replace browser sessions |
| Browser launched/owned by DeskFuse | **Chromium DevTools Protocol (CDP)**: `Emulation.setDeviceMetricsOverride` for CSS viewport and `deviceScaleFactor`; test `Page.startScreencast` / `Page.captureScreenshot` | Can request a phone-like CSS viewport/DPR independent of Windows monitor scale in a controlled browser | Screencast frames are image-encoded/transported via CDP; **not** a verified low-latency zero-copy GPU pipe; prove final physical pixel dimensions, actual JS DPR, frame rate, latency and input fidelity |
| Browser rendered offscreen by a DeskFuse-managed host | **Electron offscreen shared GPU texture**, or CEF accelerated offscreen paint; consider WebView2 composition hosting | Application/backend controls viewport and rendering policy, subject to actual API behavior | Research/browser POC needed for texture lifetime, popup behavior, profile/auth state, GPU copy count, FPS, video/DRM and app packaging |
| Complete desktop or unsupported window workflow | Dedicated monitor/capture path | Standard Windows display DPI restrictions apply; no false 350% claims | Separate deliberately selected experience, not the default card/window view |

**No chosen browser backend yet.** Evaluate against the shared native Windows path before selecting it: sharpness, actual DPI/CSS viewport, GPU texture acquisition, end-to-end phone latency, audio, permissions, WebGL/video, form inputs, clipboard, auth/profile isolation, and resource use. Start with one **managed browser** smoke test, not a wholesale migration of existing browser tabs.

Documentation:
- Chromium CDP `Emulation.setDeviceMetricsOverride`: https://chromedevtools.github.io/devtools-protocol/tot/Emulation/#method-setDeviceMetricsOverride
- Chromium CDP `Page.startScreencast`: https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-startScreencast
- Electron GPU offscreen shared textures: https://www.electronjs.org/docs/latest/tutorial/offscreen-rendering
- Electron shared texture lifetime/handle details: https://www.electronjs.org/docs/latest/api/structures/offscreen-shared-texture
- CEF accelerated offscreen paint lifecycle (CefSharp implementation example): https://cefsharp.github.io/api/130.1.x/html/M_CefSharp_OffScreen_IRenderHandler_OnAcceleratedPaint.htm
- Microsoft WebView2 composition controller overview: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/overview-features-apis
- Microsoft Win32 WGC HWND sample: https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/cpp/ScreenCaptureforHWND

**Terminology:** A browser CSS `devicePixelRatio=3.5` is *not* the same claim as an arbitrary native PMv2 HWND receiving 336 OS DPI. Validate each pipeline against the property it promises; for zero-resampling output, verify the physical pixel size at encoder input and at the decoded phone frame.

## 4. Phone UX / virtual desk behavior

- **Overview:** a pan/zoom 2D desk with optional depth/group styling. Cards represent windows, browser sessions and document activities; group by topic, project, task or user-created collection. No assumption of one-to-one correspondence between a physical Windows desktop layout and the desk's card coordinates.
- **Focus:** tap/zoom a card to a dedicated phone-native focused presentation. Prefer crisp native-resolution output at the selected phone DPR. Preserve physical pixel mapping and do not silently upscale a smaller buffer.
- **Return / arrange:** pinch or back to zoom out; drag cards and groups, multi-select, create/rename/reorder topics; preserve workspace layout across reconnects. Store desk positions in DeskFuse, not solely in Windows screen coordinates.
- **Snapshots and previews:** focused window gets high frame rate and priority; other cards can use reduced-FPS preview textures or cached thumbnails. Avoid running N full-resolution encoders unnecessarily. Prioritize privacy: hide sensitive apps, protected content and previews by policy.
- **Window lifecycle:** handle HWND re-creation, app restart, tab navigation, duplicate titles, background/minimized windows, owned dialogs and independently created popups. Keep stable logical identities distinct from ephemeral HWND values. Never claim WGC parent HWND includes all dialogs.
- **Input:** map phone touch/scroll/keyboard/IME coordinates through zoom and source pixel geometry. Focus and input ownership must be explicit; do not globally `ClipCursor` or intercept unrelated physical-display input. Multi-source drag/drop and clipboard are separate design stages.
- **Communication:** audio, camera, mic and conferencing require their own route/permissions; screen pixels alone do not solve those needs.
- **Desktop mode:** manually selectable full Windows desktop feed remains an escape hatch for complex apps, game launchers, modal system flows and unsupported capture conditions.

Potential presentation choices (not yet decided): compose the desk/cards on **Android locally** using source thumbnails and metadata, or compose a unified workspace texture on the host. A local phone composer can avoid encoding every card at full size but needs a rigorous freshness/latency budget; compare both, do not assume it is faster.

## 5. Per-phone monitor identity and authoritative scaling verification

A finding from the streamers discussion is worth carrying into all virtual-monitor-backed source pipelines:

- Map each **enrolled logical phone/client** to a stable per-client virtual display identity so Windows *may* retain that client's preferred resolution, scale and layout across reconnects. Do not assume identity alone guarantees persistent Windows preferences; inspect the OS-visible identifier. The existing DeskFuse path constructs deterministic serials via `PhysicalSize.StableSerial(phone.DeviceId)` in `AdaptiveViewportCalibration`.
- **Every attach/reconnect** must query the actual virtual monitor caps/current scale. If target 350% is supported but not active, reapply it on the **identified virtual target only**, settle, and independently verify **PMv2 HWND DPI=336** plus actual **350x350 physical pixels** for a 100-DIP ruler *before* allowing a "true native DPR" session.
- A `-3 current=350%` stored value is **not** effective DPI proof. Our unsupported 1440x3120 tests recorded 350% stored but independent native HWND at 216 DPI. Never touch physical monitor DPI, global custom scaling, user registry or sign-out settings as a shortcut.
- Sequential client A -> A -> B -> A test, across agent restart when possible; ensure client B cannot inherit A's preferences. One installed FocusIdd monitor can be shared system-wide among worktrees: tests must remain sequential. A session that owns the monitor must never be displaced by a POC.

These requirements are already detailed as **mandatory** in GPU/WGC POC spec v1.1; do not silently remove them when implementing future source adapters.

## 6. Scope boundaries and research sequence

**Current GPU/WGC coding agent:** implement one standalone native PMv2 window, exact 1440x3120 GPU WGC capture on supported 2100x3120@350%, hardware encode proof if feasible, pixel/DPI measurements, safety/teardown, identity/reconnect checks, and honest benchmarks. **Do not require it to implement CDP, Electron, the window inventory, spatial desk or multi-source streaming.** Its report should identify what an eventual adapter needs for a product-grade WindowSource interface.

**Separate browser research spike (future):**
1. Managed Chromium at CSS viewport matching phone DIPs, DPR matching phone; independently measure output physical pixels.
2. Compare CDP screencast/screenshot throughput against Electron GPU shared-texture offscreen rendering, CEF offscreen accelerated paint, and WGC fallback as appropriate. Do not assume `Page.startScreencast` is efficient enough for 60 FPS.
3. Evaluate input, text/IME, WebGL/animated content, popups, cookies/SSO, extension expectations, site permissions, video/audio, secure process isolation and CPU/GPU/memory costs.
4. Only then choose one browser backend for selected *managed* browser sources. Existing ordinary browser windows retain WGC capture unless explicitly migrated.

**Window/desk UX prototype (future):** implement a phone-native card grid/2.5D desk from **static/synthetic windows** before building multi-source live streams. Test selecting/focusing, organizing topics, drag/pan/zoom, back navigation, input routing and source persistence; then substitute real Windows window enumeration and pipelines.

**Whole-desktop fallback (future):** separate user command and quality profile, not an implicit mode switch that claims exact phone DPR.

**Approach A remains a separate non-negotiable research acceptance:** **exact 1440x3120 virtual monitor** with an independent native PMv2 336-DPI HWND and 350x350 actual captured pixels on a 1440x3120 frame. The window-first/GPU architecture is valuable even if Approach A remains blocked and is not evidence that this exact monitor objective has been achieved.

## 7. Suggested source interface (design sketch only)

```text
WindowSource { logicalId, sourceKind, appIdentity, ownerClientId,
               title, bounds, capabilities, captureStatus }
RenderRequest { sourceId, physicalWidth, physicalHeight, deviceScaleFactor,
                targetFps, focused, codecPreference }
Frame { sourceId, physicalWidth, physicalHeight, pixelFormat,
        timestamp, gpuHandleOrSurface, colorSpace, cropOrResizeFlags }
InputTarget { sourceId, focusLease, transform, pointerKeyboardImeCaps }
DeskCard { sourceId, topicGroupId, spatialTransform, previewPolicy,
           lastKnownState }
```

This interface deliberately distinguishes *source identity* from *capture backend*, *phone pixels* from *Windows monitor modes*, and *window focus* from *desk card focus*. These are **design suggestions**, not an API migration order for the present coding agent.

## 8. Decision rules / non-goals

- Don't make "stream the whole display" the central product abstraction. A window- or activity-first experience should use whichever rendering backend is correct for that source.
- Don't promise CDP screencast or browser zoom gives an efficient GPU-only path, or that arbitrary pre-existing browser HWNDs can be transparently converted to controlled offscreen sessions.
- Don't assume an empty padded display costs zero GPU time, WGC always includes popups, or a captured window continues producing frames while minimized.
- Don't introduce injection/hooks, stealth browser debugging endpoints, system-wide DPI scaling, or changes to the known-good FocusIdd driver to achieve early demos.
- Don't run all cards as 60-FPS encoded videos while only one is in focus.
- Don't equate UI fidelity with a `GetDpiForWindow` return value alone. Validate actual captured/decoded pixels.
- Don't treat this strategy note as an instruction to merge experimental work into `main` or to change the already assigned WGC POC.


## 9. Additional product-design research: native touch, gamepads and privileged sessions

**Scope note:** These are future DeskFuse product ideas requested after the WGC POC was delegated. They are **not an expansion of the GPU/WGC agent's assignment** and are not evidence that the related capabilities work.

**1:1 phone input.** Use a focused `WindowSource` and explicit input ownership to transform phone coordinates (accounting for desk zoom/pan, window origin, cropping, monitor layout and user DPI) into **real Windows virtual-screen coordinates**. Investigate native PT_TOUCH/PT_PEN using Microsoft's [`CreateSyntheticPointerDevice` / `InjectSyntheticPointerInput`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput); its `ptPixelLocation` uses virtual-screen pixel positions and supports touch contact arrays. Maintain normal mouse, keyboard, IME, wheel and shortcuts as independent options. Do not seize the physical user's pointer or assume `ClipCursor` is a safe default.

**Games.** Optional gamepad synthesis may help game streaming; historical ViGEm/ViGEmBus is a possible compatibility reference but [upstream is archived/retired](https://docs.nefarius.at/projects/ViGEm/End-of-Life/). Explore maintained alternatives and driver signing, permission, anti-cheat and lifecycle risks before any production decision. No unsolicited driver installation.

**Service / secure-desktop investigation.** Sunshine/Apollo-like use cases motivate a small privileged broker/service for supported machine-scoped actions, but **a SYSTEM service in Session 0 cannot simply capture/control the user's UAC secure desktop, lock screen or Ctrl+Alt+Del**. Microsoft [discourages interactive services](https://learn.microsoft.com/en-us/windows/desktop/services/interactive-services) and recommends user-session processes for UI work; Windows' [`SendSAS`](https://learn.microsoft.com/en-us/windows/win32/api/sas/nf-sas-sendsas) has additional configuration and authorization constraints. Research separately for each protected state and distinguish *observability* from *control*. Proposed split: authenticated, least-privilege **interactive per-user capture/input agent** plus a **minimal privileged broker** for specific authorized functionality. Do not run the media/network stack as SYSTEM by default or promise bypasses of UAC, Winlogon, protected desktops or credentials.

**Success criteria for future design research:** platform permissions, exact support matrix, security threat model, signed/installable components, consent and revocation, per-user-session boundaries, touch/gamepad injection fidelity and physical-monitor isolation. This is not part of the completed return-handshake campaign.

**Status as of 20 October 2026:** The previous v0.3 return-handshake test campaign is concluded per the owner; exact 1440×3120/336-DPI Approach A remains open. A separate agent owns the GPU/WGC POC. This document records product direction and verifiable API candidates. No browser OSR prototype, source selection policy or spatial-desk product has yet been demonstrated on the DeskFuse test PC.
