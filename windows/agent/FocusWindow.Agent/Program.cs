using System.Diagnostics;
using System.Globalization;
using FocusWindow.Agent.Display;
using FocusWindow.Agent.Input;
using FocusWindow.Agent.Media;
using FocusWindow.Agent.Interop;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Session;
namespace FocusWindow.Agent;
internal static class Program
{
    private const string Version = "0.3.0-dev";
    // Verify the EXACT published executable before touching Windows PnP.
    // This source includes new P2.3 changes. Do not impersonate the verified v0.2.29 host:
    // the existing safety proof scripts intentionally reject this candidate build.
    private const string ProbeBuildId = "DeskFuse-v0.3-P2-HARDENED-CANDIDATE-UNVERIFIED";
    private const string Usage = """
        Focus Window host agent
          FocusWindow.Agent                       run the host (pair a phone, stream the phone display)
          FocusWindow.Agent density-probe         CREATE the phone monitor, then MEASURE what Windows really did
          FocusWindow.Agent dpi-challenge         FA/BB DPI lab: PLAN ONLY unless explicitly armed
          FocusWindow.Agent driver-status         talk to the display driver and print its state
          FocusWindow.Agent display-paths         read current Windows CCD display names (NO driver installation)
          FocusWindow.Agent audio-devices          list Windows audio endpoints (READ ONLY)
          FocusWindow.Agent speakers devices      list active outputs, Bluetooth radios and paired audio sources
          FocusWindow.Agent speakers receive --source-id ID --output-name MM-1
                                                  receive native phone Bluetooth audio on the PC
          FocusWindow.Agent hold-gate-self-test    test release-file gate (NO driver installation)
          FocusWindow.Agent --version
        Host options:
          --port N (47810)   --bind IP (0.0.0.0)   --fps N (60)   --bitrate KBPS (20000)
          --workspace-app EXE  absolute path to a NEW application instance for GPU sessions
          --workspace-args TEXT  local launch arguments (for example, an app's new-instance flag)
                             Existing physical windows are never moved; app documents survive disconnect.
          --auto-approve     legacy QR test-mode only; does NOT bypass first-time LAN enrollment
          --legacy-qr       explicitly enable historical QR pairing instead of phone discovery
          trusted-phones    list enrolled device IDs (for review and revocation)
          trusted-phones --forget-phone ID   revoke a phone and its approvals
          --new-pairing      rotate the host public key (all phones must re-enroll)
          --single-session   exit after one phone session
          --experimental-mic-cable --phone-mic-output-id ID --phone-mic-device-id DEVICE_ID
                             DEV BRIDGE: opt in to route authorized Android microphone
                             into explicitly selected Windows render endpoint ID.
                             Select a virtual cable's corresponding RECORDING device
                             in Windows apps. This does NOT install a DeskFuse mic.
                             Never select physical speakers for this adapter.
          --enable-pc-audio  allow approved phones to request mixed PC playback sound
                             (OFF by default; may include audio from other PC apps)
          --ruler-probe      verify rendered density with a pixel ruler at the start of every session
          --adaptive-dpi     legacy alias: native-DPI virtual backing monitor (now default)
          --disable-native-dpi  explicit diagnostic compatibility mode; GPU path
                                will refuse unverified phone-native DPI
          --no-open-qr       do not open pairing.png automatically
          --log PATH
        density-probe options (defaults describe a 1440x3120, 3.5x, 6.7-inch phone):
          --width N --height N --dpr F --diag-mm N --refresh N
          --launch EXE       also start EXE, move ONLY that window onto the phone monitor and report its DPI
          --out DIR          where to write density-evidence-*.md/.json (default: current directory)
          --hold-until-flag PATH   safety-runner only: keep the 1440x3120 monitor attached until OK releases it
          --experimental-scale-write    UNSUPPORTED Windows DPI API; explicit opt-in on a test PC only
        dpi-challenge options (NO driver install/uninstall, NO physical-monitor SET):
          --modes 1440x3120,1600x3120,1920x3120,2100x3120,2240x3120,2240x3360
          --diag-mm N --refresh N --settle-ms N --out DIR
          --same-serial                                reuse one EDID serial across modes (regression test)
          --serial N                                   fixed 32-bit EDID serial (host identity regression)
          --run --i-accept-virtual-display-test       explicitly create temporary virtual modes
          --try-350                                    attempt undocumented -4 only if legal
          --try-above-cap --i-accept-unsupported-scale-risk   also attempt 350% beyond cap at 1440x3120
        """;
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--probe-build-id")) { Console.WriteLine(ProbeBuildId); return 0; }
        if (args.Contains("--version")) { Console.WriteLine(Version); return 0; }
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down; removing the phone monitor...");
            shutdown.Cancel();
        };
        try
        {
            var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "run";
            if (command is "run" or "density-probe" or "dpi-challenge") RequirePerMonitorV2();
            return command switch
            {
                "run" => await RunHostAsync(args, shutdown.Token),
                "density-probe" => await DensityProbeAsync(args, shutdown.Token),
                "dpi-challenge" => await DprChallengeLab.RunAsync(args, shutdown.Token),
                "driver-status" => DriverStatus(),
                "display-paths" => PrintDisplayPaths(),
                "trusted-phones" => TrustedPhones(args),
                "audio-devices" => ListAudioDevices(),
                "speakers" => await DeskFuse.Audio.SpeakerCommands.RunAsync(args.Skip(1).ToArray(), shutdown.Token),
                "hold-gate-self-test" => await HoldGateSelfTestAsync(),
                _ => Fail($"Unknown command '{command}'.\n\n{Usage}"),
            };
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (DisplayException ex)
        {
            Console.Error.WriteLine($"[{ex.Stage}] {ex.Message}");
            if (ex.Remediation is not null) Console.Error.WriteLine("  -> " + ex.Remediation);
            return 1;
        }
    }
    /// <summary>Read-only audio endpoint discovery for explicit cable routing.
    /// Does not change Windows default inputs/outputs or device state.
    /// </summary>
    private static int ListAudioDevices()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(
            NAudio.CoreAudioApi.DataFlow.All, NAudio.CoreAudioApi.DeviceState.All);
        foreach (var item in devices)
        {
            using (item)
            {
                // A stale/unplugged MMDevice can lose its property store while
                // it is enumerated. One such entry must not hide usable outputs.
                try
                {
                    Console.WriteLine($"{item.DataFlow} / {item.State}: {item.FriendlyName}");
                    Console.WriteLine($"  id: {item.ID}");
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    Console.WriteLine($"Unavailable audio endpoint: {item.ID}");
                }
            }
        }
        Console.WriteLine("For the development mic bridge use a virtual cable's RENDER endpoint ID.");
        Console.WriteLine("Select its corresponding CAPTURE endpoint in Windows applications.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
    /// <summary>
    /// The probe must use a PerMonitorV2 window to measure the rendered ruler in physical pixels.
    /// GetDpiForMonitor is retained as a supplemental reading but is not sufficient proof
    /// for a per-monitor-aware caller. The manifest requests PMv2 and this verifies it.
    /// </summary>
    private static void RequirePerMonitorV2()
    {
        var ctx = Native.GetThreadDpiAwarenessContext();
        if (!Native.AreDpiAwarenessContextsEqual(ctx, Native.CtxPerMonitorV2))
        {
            throw new DisplayException("process",
                "The agent is not running as PerMonitorV2 DPI-aware, so every DPI and rectangle it read would be virtualised.",
                "Run the published FocusWindow.Agent.exe, which embeds the PerMonitorV2 manifest (not `dotnet run` under a host that overrides it).");
        }
    }
    // ---- argument helpers ---------------------------------------------------------------------------
    private static string? Opt(string[] args, string name) => CliArguments.Value(args, name);
    private static int Int(string[] args, string name, int fallback) =>
        CliArguments.Int(args, name, fallback);
    private static double Dbl(string[] args, string name, double fallback) =>
        CliArguments.Double(args, name, fallback);
    // ---- run ----------------------------------------------------------------------------------------
    private static async Task<int> RunHostAsync(string[] args, CancellationToken ct)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator))
            return Fail("Run the network host as a normal Windows user. Installed driver access must be resolved through an approved local boundary, not an elevated LAN host.");
        using var log = new ConsoleLog(Opt(args, "--log"));
        // The cable adapter is deliberately unavailable without both flags.
        // A Windows render-endpoint ID is not a DeskFuse PnP mic identity.
        var microphoneOutputId = Opt(args, "--phone-mic-output-id");
        var microphoneDeviceId = Opt(args, "--phone-mic-device-id");
        var allowMicrophone = args.Contains("--experimental-mic-cable") &&
            !string.IsNullOrWhiteSpace(microphoneOutputId) &&
            !string.IsNullOrWhiteSpace(microphoneDeviceId);
        var options = new HostOptions
        {
            Port = Int(args, "--port", 47810),
            Bind = Opt(args, "--bind") ?? "0.0.0.0",
            Fps = Int(args, "--fps", 60),
            BitrateKbps = Int(args, "--bitrate", 20_000),
            HostVersion = Version,
            DrawRulerProbe = args.Contains("--ruler-probe"),
            NewPairing = args.Contains("--new-pairing"),
            SingleSession = args.Contains("--single-session"),
            AllowFullUserFiles = !args.Contains("--auto-approve"),
            CameraFreePairing = !args.Contains("--legacy-qr"),
            // The POC's verified larger-backing-display method is now
            // the intended product mode, not an optional experiment.
            AdaptiveDpiViewport = !args.Contains("--disable-native-dpi"),
            AllowPcAudio = args.Contains("--enable-pc-audio"),
            AllowPhoneMicrophone = allowMicrophone,
        };
        log.Info($"Focus Window host agent {Version}");
        if (allowMicrophone)
            log.Warn("Development microphone bridge ENABLED for one explicitly selected Windows playback endpoint. " +
                "Use a virtual audio cable only; a physical endpoint would play the phone microphone aloud.");
        var guards = new List<PhysicalDisplayGuard>();
        using var sources = new WorkspaceApplicationCatalog();
        WindowsDisplayBackend? backend = null;
        var brokerClient = new DeskFuse.Audio.PhoneEndpointBrokerClient();
        var services = new HostServices(
            CreateBackend: () => backend = new WindowsDisplayBackend(log),
            CreateInput: () => new SendInputSink(log),
            CreateGuard: () =>
            {
                var g = new PhysicalDisplayGuard(() => backend?.PhoneGdiName);
                guards.Add(g);
                return g;
            },
            Approval: new ConsoleApproval(args.Contains("--auto-approve"), log),
            PairingStore: new DpapiPairingStore(),
            DeviceCredentials: new DpapiDeviceCredentialStore(),
            Clipboard: new WindowsClipboardBridge(),
            Windows: new WindowsWindowWorkflow(),
            CreateVideoPipeline: () => new NativeGpuPipeline(Opt(args, "--workspace-app"), Opt(args, "--workspace-args"), sources),
            CreateAudioCapture: (layouts, maxBytes) => new WindowsLoopbackAudioCapture(layouts, maxBytes),
            // The phone speaker route captures the phone's own endpoint only.
            CreateEndpointAudioCapture: (endpointId, layouts, maxBytes) =>
                new WindowsLoopbackAudioCapture(endpointId, layouts, maxBytes),
            PhoneSpeakers: new BrokerPhoneSpeakerProvider(brokerClient),
            CreatePhoneMicrophoneSink: allowMicrophone
                ? id => string.Equals(id, microphoneDeviceId, StringComparison.Ordinal)
                    ? new WindowsPhoneMicrophoneCableSink(microphoneOutputId!)
                    : null
                : null,
            CanUsePhoneMicrophone: allowMicrophone
                ? id => string.Equals(id, microphoneDeviceId, StringComparison.Ordinal)
                : null,
            OnNegotiated: outcome =>
            {
                var path = Path.Combine(AppData.Directory, "last-session-evidence.md");
                File.WriteAllText(path, outcome.Evidence.ToMarkdown("Last session density evidence"));
                log.Info("density evidence written to " + path);
            });
        using var host = new SessionHost(options, services, log);
        var offer = await host.StartAsync(ct);
        if (!options.CameraFreePairing)
            PairingQr.Show(offer, open: !args.Contains("--no-open-qr"));
        else
            Console.WriteLine("  DeskFuse is discoverable on the LAN. Approve a new phone on this PC once.");
        Console.WriteLine("  Waiting for a phone. Ctrl+C to quit.\n");
        await host.RunSessionsAsync(ct);
        return 0;
    }
    private static int TrustedPhones(string[] args)
    {
        var store = new DpapiDeviceCredentialStore();
        var forget = Opt(args, "--forget-phone");
        if (!string.IsNullOrEmpty(forget))
        {
            store.Revoke(forget);
            using var log = new ConsoleLog(null);
            new ConsoleApproval(false, log).ForgetDevice(forget);
            Console.WriteLine("Phone authorization revoked. Existing sessions remain active until disconnected.");
            return 0;
        }
        foreach (var item in store.All())
            Console.WriteLine(item.DeviceId);
        return 0;
    }

    // ---- driver-status ------------------------------------------------------------------------------
    // Read-only P/Invoke smoke test: check full UTF-16 CCD names on physical displays
    // BEFORE installing the virtual display. A one-character GDI/name means the
    // marshaling regression persists and the live test must not run.
    private static int PrintDisplayPaths()
    {
        Console.WriteLine(DisplayConfigApi.DescribePaths());
        return 0;
    }
    private static int DriverStatus()
    {
        using var driver = DriverDevice.Open();
        var version = driver.Ping();
        var s = driver.GetStatus();
        Console.WriteLine($"device           {driver.DevicePath}");
        Console.WriteLine($"protocol         driver {version}, agent {DriverContract.ProtocolVersion} {(version == DriverContract.ProtocolVersion ? "(match)" : "(MISMATCH)")}");
        Console.WriteLine($"monitor arrived  {s.MonitorArrived}   {s.WidthPx}x{s.HeightPx} @ {s.RefreshHz} Hz");
        Console.WriteLine($"frames           published {s.FramesPublished}, dropped {s.FramesDropped}");
        Console.WriteLine($"last error       0x{s.LastError:X8}   surface win32 error {s.SurfaceWin32Error}");
        return version == DriverContract.ProtocolVersion ? 0 : 1;
    }
    /// <summary>
    /// Mirrors PhoneSession's keepalive while the temporary density-probe session is
    /// waiting on Windows display discovery, measuring DPI, and tearing down.
    /// The driver's 10-second dead-agent removal timer stays enabled.
    /// Disposed before WindowsDisplayBackend (reverse order of using declarations).
    /// </summary>
    private sealed class ProbeDriverHeartbeat : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public ProbeDriverHeartbeat(WindowsDisplayBackend backend)
        {
            _loop = Task.Run(async () =>
            {
                try
                {
                    var tick = 0;
                    while (!_stop.IsCancellationRequested)
                    {
                        // GET_STATUS is itself an IOCTL watchdog heartbeat. Log the
                        // driver's actual monitor state to distinguish voluntary departure
                        // from Windows deactivation or a dead UMDF device.
                        var health = backend.ReadDriverHealth();
                        Console.WriteLine($"  keepalive #{++tick}: reachable={health.Reachable} " +
                            $"arrived={health.MonitorArrived} " +
                            $"frames={health.FramesPublished} error=0x{health.LastError:X8}");
                        await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop.ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }

    /// <summary>
    /// Keep the monitor alive until the safety-runner releases it AFTER the user
    /// clicks OK. Never interpret a non-native DPI result as permission to remove
    /// the monitor. If Windows or the driver drops the monitor first, fail closed.
    /// </summary>
    private static async Task<TimeSpan> HoldMonitorUntilReleaseAsync(
        string releaseFlag, Func<bool> monitorPresent, CancellationToken ct,
        Action<TimeSpan>? onHeartbeat = null)
    {
        var held = Stopwatch.StartNew();
        var ticks = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!monitorPresent())
                throw new IOException("Virtual monitor departed before the safety OK button was clicked.");
            if (File.Exists(releaseFlag))
                return held.Elapsed;
            if (++ticks % 10 == 0)
                onHeartbeat?.Invoke(held.Elapsed); // every ~5 s while awaiting confirmation
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Driver-free smoke test: prove that the hold gate cannot finish without its
    /// release file, then that the release unblocks it. Also test lost-monitor fail-fast.
    /// This runs from the clean-build verifier BEFORE any driver is installed.
    /// </summary>
    private static async Task<int> HoldGateSelfTestAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeskFuse-hold-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var release = Path.Combine(dir, "release.flag");
            var hold = HoldMonitorUntilReleaseAsync(release, () => true, CancellationToken.None);
            await Task.Delay(1250);
            if (hold.IsCompleted)
                return Fail("HOLD GATE FAIL: probe released before OK flag was created.");
            File.WriteAllText(release, "OK");
            var duration = await hold.WaitAsync(TimeSpan.FromSeconds(5));
            if (duration.TotalMilliseconds < 1200)
                return Fail("HOLD GATE FAIL: release was unexpectedly early.");
            try
            {
                await HoldMonitorUntilReleaseAsync(Path.Combine(dir, "absent.flag"),
                    () => false, CancellationToken.None);
                return Fail("HOLD GATE FAIL: lost monitor was not detected.");
            }
            catch (IOException) { /* expected */ }
            Console.WriteLine("PASS: release requires OK flag; lost monitor aborts; no driver installed.");
            return 0;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- density-probe ------------------------------------------------------------------------------
    /// <summary>
    /// The demonstration: create the phone monitor for a given phone, run the whole negotiation ladder, and
    /// print every check as expected-vs-measured. It ends with one of two sentences and an exit code:
    ///   NATIVE DENSITY VERIFIED  (0)  - Windows is rendering the phone monitor at the phone's density
    ///   NOT NATIVE: ...          (1)  - with the measured reason
    /// </summary>
    private static async Task<int> DensityProbeAsync(string[] args, CancellationToken ct)
    {
        var width = Int(args, "--width", 1440);
        var height = Int(args, "--height", 3120);
        var dpr = (float)Dbl(args, "--dpr", 3.5);
        var diag = Int(args, "--diag-mm", 170);
        var refresh = Int(args, "--refresh", 60);
        var outDir = Opt(args, "--out") ?? Directory.GetCurrentDirectory();
        var launch = Opt(args, "--launch");
        var holdUntilFlag = Opt(args, "--hold-until-flag");
        if (args.Contains("--hold-until-flag") && string.IsNullOrWhiteSpace(holdUntilFlag))
            throw new ArgumentException("--hold-until-flag requires a path.");
        using var log = new ConsoleLog();
        var profile = new PhoneProfile(1, "density-probe", "Density probe", width, height, dpr, 0, 0, 0, 0, 1f, refresh, diag,
            width >= height ? "LANDSCAPE" : "PORTRAIT", "PHONE");
        Console.WriteLine($"Density probe for {width}x{height} @ {dpr}x ({diag} mm diagonal, {refresh} Hz)");
        Console.WriteLine($"  host build identifier: {ProbeBuildId}");
        Console.WriteLine($"  phone wants:   {profile.RequestedScalePercent}%  = {profile.ExpectedDpi} DPI  = {profile.CanvasDpW:0.#} x {profile.CanvasDpH:0.#} dp");
        Console.WriteLine();
        await using var backend = new WindowsDisplayBackend(log);
        // Open the control connection before starting the timer task, so only
        // one thread can initialize the driver's handle.
        var initialHealth = backend.ReadDriverHealth();
        Console.WriteLine($"  probe driver initial state: reachable={initialHealth.Reachable}");
        // The production PhoneSession sends heartbeats, but the one-off density probe
        // used to block for up to 12 seconds in CCD enumeration without any IOCTL.
        // FocusIdd intentionally departs its monitor after 10 seconds of IOCTL silence.
        // Keep that exact safety watchdog; send a PING throughout the probe instead.
        await using var probeHeartbeat = new ProbeDriverHeartbeat(backend);
        Console.WriteLine("  DeskFuse probe keepalive v0.2.26: STATUS IOCTL every 1 s (driver timeout 10 s)");
        var guard = new PhysicalDisplayGuard(() => backend.PhoneGdiName);
        var before = guard.Snapshot();
        Console.WriteLine($"  physical monitors before: {before.Monitors.Count}");
        if (before.Monitors.Any(m => m.Dpi == 0))
            Console.WriteLine("  WARNING: Some physical-monitor DPI measurements unavailable; snapshot is incomplete.");
        EvidenceLog evidence;
        NegotiationOutcome? outcome = null;
        var holdFailed = false;
        try
        {
            var experimentalWrite = args.Contains("--experimental-scale-write");
            if (experimentalWrite)
                Console.Error.WriteLine("WARNING: experimental undocumented DPI-setting API enabled for this probe.");
            var negotiator = new DensityNegotiator(backend,
                new NegotiatorOptions
                {
                    DrawRuler = true,
                    AllowExperimentalScaleWrite = experimentalWrite,
                    PreserveRequestedResolution = true // evidence mode: never tear down/recreate just to chase a DPI cap
                }, log);
            outcome = await negotiator.RunAsync(profile, (stage, detail) => Console.WriteLine($"  .. {stage}: {detail}"), ct);
            evidence = outcome.Evidence;
            if (launch is not null) CheckLaunchedApp(launch, outcome, evidence);
        }
        catch (DisplayException ex)
        {
            evidence = new EvidenceLog();
            evidence.Fail("setup-" + ex.Stage, "Could not set up the phone monitor", "a working phone monitor", ex.Message, ex.Remediation);
        }
        catch (OperationCanceledException)
        {
            evidence = new EvidenceLog();
            evidence.Fail("setup-cancelled", "Probe cancelled", "completed measurement", "cancelled by operator");
        }
        catch (Exception ex)
        {
            evidence = new EvidenceLog();
            evidence.Fail("setup-unhandled", "Probe failed unexpectedly", "successful probe", ex.ToString(),
                "Investigate the exception before continuing; this is not a passing test.");
        }
        // In the guarded driver test, KEEP the identified monitor connected even
        // when the density verdict is NOT NATIVE. Only the safety dialog's OK
        // creates the release file. A premature driver/Windows departure is a
        // hard failure and triggers the independent driver rollback.
        if (holdUntilFlag is not null)
        {
            if (outcome is null)
            {
                holdFailed = true;
                Console.Error.WriteLine("HOLD FAILED: density setup did not return a working monitor.");
            }
            else
            {
                try
                {
                    var heldHealth = backend.ReadDriverHealth();
                    if (!heldHealth.Reachable || !heldHealth.MonitorArrived)
                        throw new IOException("Monitor was not present when the hold phase began.");
                    Directory.CreateDirectory(outDir);
                    var readyFlag = Path.Combine(outDir, "monitor-held.flag");
                    // A fresh, per-test folder is created by the safety runner.
                    File.WriteAllText(readyFlag, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                    Console.WriteLine("  HOLD ACTIVE: 1440x3120 monitor remains attached until safety OK; no DPI fallback.");
                    Console.Out.Flush();
                    var heldFor = await HoldMonitorUntilReleaseAsync(holdUntilFlag,
                        () =>
                        {
                            var health = backend.ReadDriverHealth();
                            return health.Reachable && health.MonitorArrived;
                        }, ct,
                        elapsed => Console.WriteLine($"  HOLD STILL ACTIVE: monitor arrived=True; awaiting OK ({elapsed.TotalSeconds:0}s)")).ConfigureAwait(false);
                    evidence.Pass("confirmation-gated-lifetime", "Monitor held until safety OK",
                        "driver monitor attached until release", $"held through OK for {heldFor.TotalSeconds:0.0} s",
                        "Driver continued to report monitor arrived while waiting for the release flag.");
                    Console.WriteLine($"  HOLD RELEASED: OK received after {heldFor.TotalSeconds:0.0} s; now removing monitor.");
                }
                catch (Exception ex)
                {
                    holdFailed = true;
                    evidence.Fail("confirmation-gated-lifetime", "Monitor held until safety OK",
                        "monitor attached until OK", ex.Message,
                        "The driver watchdog and guarded runner must roll back the temporary device.");
                    Console.Error.WriteLine("HOLD FAILED: " + ex.Message);
                }
            }
        }
        await backend.RemoveMonitorAsync(CancellationToken.None);
        await Task.Delay(1500, CancellationToken.None); // let Windows finish the departure
        var report = guard.Verify(before);
        if (report.Clean)
            evidence.Pass("physical-displays-unchanged", "Physical monitors unchanged after teardown", "identical", "identical");
        else
            evidence.Fail("physical-displays-unchanged", "Physical monitors unchanged after teardown", "identical", string.Join("; ", report.Drift));
        var verified = outcome is { Native: true } && !evidence.HasFailure;
        var verdict = verified
            ? "NATIVE DENSITY VERIFIED: Windows rendered the phone monitor at the phone's density, measured in pixels."
            : "NOT NATIVE: " + (outcome?.DegradedReason ?? evidence.Items.FirstOrDefault(i => i.Status == EvidenceStatus.Fail)?.Actual ?? "see the failed checks above");
        var markdown = evidence.ToMarkdown("Density evidence") + "\n**" + verdict + "**\n";
        Console.WriteLine();
        Console.WriteLine(markdown);
        Directory.CreateDirectory(outDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var md = Path.Combine(outDir, $"density-evidence-{stamp}.md");
        var json = Path.Combine(outDir, $"density-evidence-{stamp}.json");
        await File.WriteAllTextAsync(md, markdown, CancellationToken.None);
        await File.WriteAllTextAsync(json, evidence.ToJsonString(), CancellationToken.None);
        Console.WriteLine($"written: {md}\n         {json}");
        // Exit 1 means the DPI is NOT NATIVE but the monitor survived until OK.
        // Exit 2 means the monitor hold/setup failed; the runner MUST roll back.
        return holdFailed ? 2 : verified ? 0 : 1;
    }
    /// <summary>
    /// Starts a real application, moves ONLY that window (one this agent launched) onto the phone monitor,
    /// and reports the DPI Windows gives it and its awareness. This is what a real app is told; the pixel-
    /// level ruler above proves the rendering path with a window we control.
    /// </summary>
    private static void CheckLaunchedApp(string exe, NegotiationOutcome outcome, EvidenceLog evidence)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            if (process is null) { evidence.Skip("app-dpi", $"{exe}: window DPI on the phone monitor", "the process did not start"); return; }
            IntPtr hwnd = IntPtr.Zero;
            for (var i = 0; i < 100 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                process.Refresh();
                hwnd = process.MainWindowHandle;
            }
            if (hwnd == IntPtr.Zero)
            {
                evidence.Skip("app-dpi", $"{exe}: window DPI on the phone monitor", "the application created no main window within 10 s");
                return;
            }
            var b = outcome.Monitor.Bounds;
            Native.SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, Native.SwpNoZOrder | Native.SwpNoActivate);
            Thread.Sleep(1200); // let the app react to WM_DPICHANGED
            var dpi = (int)Native.GetDpiForWindow(hwnd);
            var awareness = DisplayConfigApi.DescribeAwareness(hwnd);
            if (Math.Abs(dpi - outcome.EffectiveDpi) <= 1)
                evidence.Pass("app-dpi", $"{exe}: window DPI after moving to the phone monitor", $"{outcome.EffectiveDpi}", $"{dpi}", awareness);
            else
                evidence.Fail("app-dpi", $"{exe}: window DPI after moving to the phone monitor", $"{outcome.EffectiveDpi}", $"{dpi}",
                    $"{awareness}: this app is not rendered at the phone's density; Windows bitmap-scales it.");
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(3000)) process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
