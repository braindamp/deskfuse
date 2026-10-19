using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
using Microsoft.Win32;

namespace FocusWindow.Agent.Display;

/// <summary>
/// POC-only virtual-monitor attach/hold command for the GPU-phone-workspace experiment.
///
/// It deliberately reuses the verified WindowsDisplayBackend/DriverDevice/DisplayConfigApi
/// paths instead of porting the driver protocol into the native POC. It:
///   * refuses if any monitor is already attached or a host session is listening,
///   * derives the deterministic per-client FocusIdd serial and persistent profile,
///   * attaches 2100x3120@60 only (the caller supplies the mode),
///   * reads back scale, re-applies supported 350% on every attach, and proves the
///     readback before declaring the monitor ready,
///   * records the OS-visible EDID serial/container/instance identity,
///   * holds the monitor with the driver heartbeat until the runner releases it,
///   * tears down and verifies monitor arrived 0 + physical displays unchanged.
///
/// This is a diagnostic command in the POC branch. It does not alter the standard
/// host `run` flow, the installed driver, or any physical display setting.
/// </summary>
internal static class PocMonitorHost
{
    private const string Schema = "deskfuse.gpu-poc.monitor.v1";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var clientId = Opt(args, "--client-id");
        var outDir = Opt(args, "--out");
        var releaseFlag = Opt(args, "--release-flag");
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("poc-monitor requires --client-id (an anonymized test pseudonym, not a secret).");
        if (string.IsNullOrWhiteSpace(outDir))
            throw new ArgumentException("poc-monitor requires --out DIR.");
        if (string.IsNullOrWhiteSpace(releaseFlag))
            throw new ArgumentException("poc-monitor requires --release-flag PATH.");

        var width = Int(args, "--width", 2100);
        var height = Int(args, "--height", 3120);
        var refresh = Int(args, "--refresh", 60);
        var diagMm = Int(args, "--diag-mm", 170);
        var desiredScale = Int(args, "--scale", 350);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(Int(args, "--timeout-sec", 180), 10, 3600));
        var simulateInitial = OptInt(args, "--simulate-initial-scale");
        outDir = Path.GetFullPath(outDir);
        releaseFlag = Path.GetFullPath(releaseFlag);
        Directory.CreateDirectory(outDir);

        var readyJson = Path.Combine(outDir, "monitor-ready.json");
        var readyKv = Path.Combine(outDir, "monitor-ready.kv");
        var resultJson = Path.Combine(outDir, "monitor-result.json");
        var failureJson = Path.Combine(outDir, "monitor-failure.json");

        if (!IsAdministrator())
        {
            WriteJson(failureJson, Failure("PRECHECK_ELEVATION_REQUIRED",
                "The installed FocusIdd device ACL permits only SYSTEM, Administrators and the User-Mode Driver (UD) group. " +
                "Run the guarded POC hardware phase from an elevated shell; do not change the installed driver ACL."));
            return 10;
        }
        if (WaitForReleaseTimeout(releaseFlag, TimeSpan.Zero))
        {
            WriteJson(failureJson, Failure("PRECHECK_RELEASE_FLAG_STALE",
                $"The release flag already exists: {releaseFlag}"));
            return 10;
        }
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        var busy = listeners.Where(l => l.Port is 47810 or 47812).Select(l => l.Port).Distinct().ToArray();
        if (busy.Length > 0)
        {
            WriteJson(failureJson, Failure("PRECHECK_ACTIVE_HOST_SESSION",
                $"TCP port(s) {string.Join(", ", busy)} are listening; a DeskFuse host/enrollment session may own the display. Stop it first."));
            return 10;
        }

        var stableSerial = PhysicalSize.StableSerial(clientId);
        var profileDir = Opt(args, "--profile-dir") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskFuse", "gpu-poc", "clients");
        Directory.CreateDirectory(profileDir);
        var profilePath = Path.Combine(profileDir, ProfileName(clientId) + ".json");
        var profile = ClientProfile.Load(profilePath);
        if (profile is not null && profile.StableSerial != stableSerial)
        {
            WriteJson(failureJson, Failure("IDENTITY_MISMATCH",
                $"The stored profile for '{clientId}' has serial 0x{profile.StableSerial:X8}, but the deterministic serial is 0x{stableSerial:X8}. " +
                "Refusing to merge or borrow another identity."));
            return 11;
        }
        var attachCount = profile?.AttachCount ?? 0;
        var (mmW, mmH) = PhysicalSize.FromDiagonal(diagMm, width, height);

        using var log = new ConsoleLog();
        await using var backend = new WindowsDisplayBackend(log);
        var guard = new PhysicalDisplayGuard(() => backend.PhoneGdiName);
        var physicalBefore = guard.Snapshot();

        var watch = Stopwatch.StartNew();
        JsonObject? ready = null;
        var result = new JsonObject();
        var failure = (JsonObject?)null;
        var exitCode = 0;
        var readyAt = TimeSpan.Zero;
        string holdReason = "not_started";
        TimeSpan held = TimeSpan.Zero;

        try
        {
            var healthBefore = backend.ReadDriverHealth();
            Console.WriteLine($"POC-MONITOR preflight: reachable={healthBefore.Reachable} " +
                $"protocol={healthBefore.ProtocolVersion} arrived={healthBefore.MonitorArrived} " +
                $"mode={width}x{height}@{refresh} scale={desiredScale}% serial=0x{stableSerial:X8} " +
                $"mm={mmW}x{mmH} attachCount={attachCount}");
            if (!healthBefore.Reachable)
            {
                failure = Failure("PRECHECK_DRIVER_UNREACHABLE",
                    "The FocusIdd control device could not be opened. If this is a normal-user shell, use an elevated shell; " +
                    "do not weaken the installed driver ACL for the POC.");
                exitCode = 10;
                return exitCode;
            }
            if (healthBefore.ProtocolVersion != DriverContract.ProtocolVersion)
            {
                failure = Failure("PRECHECK_PROTOCOL_MISMATCH",
                    $"Driver protocol {healthBefore.ProtocolVersion}, expected {DriverContract.ProtocolVersion}.");
                exitCode = 10;
                return exitCode;
            }
            if (healthBefore.MonitorArrived)
            {
                failure = Failure("PRECHECK_ACTIVE_MONITOR",
                    "The driver already reports monitor arrived 1. Refusing to remove or steal another session's monitor.");
                exitCode = 10;
                return exitCode;
            }

            var topologyBefore = DisplayConfigApi.CaptureTopology();
            var monitor = await backend.CreateMonitorAsync(
                new MonitorRequest(width, height, refresh, mmW, mmH, stableSerial, desiredScale), ct)
                .ConfigureAwait(false);
            Console.WriteLine($"POC-MONITOR attached: {monitor.GdiName} {monitor.Bounds} ({monitor.Description})");

            var target = TryFindTarget(topologyBefore, width, height, out var targetDetail);
            var deviceId = DisplayIdentity.ActiveMonitorDeviceId(monitor.GdiName);
            var edid = DisplayIdentity.Read(stableSerial, deviceId);
            Console.WriteLine($"POC-MONITOR identity: deviceId='{deviceId ?? "(none)"}' edidSerial={edid.SerialHex ?? "(none)"} " +
                $"serialMatch={edid.SerialMatch} container='{edid.ContainerId ?? "(none)"}' " +
                $"instance='{edid.Instance ?? "(none)"}' {targetDetail}");
            if (!edid.SerialMatch)
                Console.WriteLine($"POC-MONITOR identity warning: {edid.Detail}");

            var caps = backend.ReadScaleCaps(monitor);
            if (caps is null)
            {
                failure = Failure("SCALE_CAPS_UNAVAILABLE",
                    "Windows returned no decodable per-monitor scale limits; cannot prove 350% on this target.");
                exitCode = 11;
                return exitCode;
            }
            var currentBefore = caps.CurrentPercent;
            var max = caps.MaxPercent;
            var recommended = caps.RecommendedPercent;
            var min = caps.MinPercent;
            Console.WriteLine($"POC-MONITOR scale before: current={currentBefore}% recommended={recommended}% " +
                $"min={min}% max={max}% desired={desiredScale}%");

            var initial100Observed = currentBefore == 100;
            var simulatedResetApplied = false;
            var simulatedResetAttempted = false;
            var setDetails = new JsonArray();
            var setAttempts = 0;
            if (simulateInitial is int simulatedTarget && simulatedTarget != desiredScale)
            {
                simulatedResetAttempted = true;
                if (currentBefore == simulatedTarget)
                {
                    initial100Observed = true;
                    Console.WriteLine($"POC-MONITOR simulated reset already observed: current={currentBefore}%");
                }
                else
                {
                    var simResult = backend.TrySetScale(monitor, simulatedTarget);
                    setDetails.Add($"simulate {simulatedTarget}%: {simResult.Ok} {simResult.Detail}");
                    if (simResult.Ok)
                    {
                        simulatedResetApplied = await WaitForScaleAsync(backend, monitor, simulatedTarget, TimeSpan.FromSeconds(6), ct)
                            .ConfigureAwait(false);
                    }
                    Console.WriteLine($"POC-MONITOR simulated reset {simulatedTarget}%: applied={simulatedResetApplied} " +
                        $"({simResult.Detail})");
                }
            }

            // On EVERY attach: read the actual value, then reapply the supported target if needed.
            // Never trust a remembered Windows preference or the mere success of an API call.
            ScaleCaps finalCaps = caps;
            var scaleSource = "UNKNOWN";
            var enforced = false;
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var now = backend.ReadScaleCaps(monitor);
                if (now is null)
                {
                    failure = Failure("SCALE_CAPS_UNAVAILABLE", "Scale limits disappeared after attach.");
                    exitCode = 11;
                    return exitCode;
                }
                if (now.CurrentPercent == desiredScale)
                {
                    finalCaps = now;
                    scaleSource = simulatedResetApplied || initial100Observed && currentBefore == 100
                        ? "DESKFUSE_REAPPLIED_SETTINGS_AFTER_100_RESET"
                        : attachCount > 0 && !simulatedResetApplied
                            ? "WINDOWS_RESTORED_SETTINGS"
                            : "WINDOWS_APPLIED_PREFERRED_ON_FIRST_ATTACH";
                    enforced = simulatedResetApplied || scaleSource.StartsWith("DESKFUSE", StringComparison.Ordinal);
                    Console.WriteLine($"POC-MONITOR scale ready: current={finalCaps.CurrentPercent}% source={scaleSource}");
                    break;
                }
                if (desiredScale > now.MaxPercent)
                {
                    failure = Failure("SCALE_REAPPLY_FAILED",
                        $"{desiredScale}% is outside this target's supported maximum {now.MaxPercent}%; " +
                        $"current is {now.CurrentPercent}%. Refusing an unsupported write.");
                    exitCode = 11;
                    return exitCode;
                }
                if (now.CurrentPercent != desiredScale)
                {
                    ScaleSetResult set;
                    try
                    {
                        set = backend.TrySetScale(monitor, desiredScale);
                    }
                    catch (Exception ex)
                    {
                        set = new ScaleSetResult(false, ex.Message);
                    }
                    setAttempts++;
                    setDetails.Add($"set {desiredScale}%: {set.Ok} {set.Detail}");
                    Console.WriteLine($"POC-MONITOR scale set #{setAttempts}: {set.Ok} {set.Detail}");
                    var applied = set.Ok &&
                        await WaitForScaleAsync(backend, monitor, desiredScale, TimeSpan.FromSeconds(8), ct)
                            .ConfigureAwait(false);
                    if (!applied)
                    {
                        if (cycle == 1)
                        {
                            failure = Failure("SCALE_REAPPLY_FAILED",
                                $"Windows did not read back {desiredScale}% after {setAttempts} attempt(s): {set.Detail}");
                            exitCode = 11;
                            return exitCode;
                        }
                        Console.WriteLine("POC-MONITOR scale set did not settle yet; retrying once.");
                        await Task.Delay(750, ct).ConfigureAwait(false);
                        continue;
                    }
                    scaleSource = simulatedResetApplied || initial100Observed
                        ? "DESKFUSE_REAPPLIED_SETTINGS_AFTER_100_RESET"
                        : "DESKFUSE_REAPPLIED_SETTINGS";
                    enforced = true;
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    var settled = backend.ReadScaleCaps(monitor);
                    if (settled is null || settled.CurrentPercent != desiredScale)
                    {
                        if (cycle == 1)
                        {
                            failure = Failure("SCALE_REAPPLY_FAILED",
                                $"Scale reverted after setting: {(settled is null ? "caps unavailable" : settled.CurrentPercent + "%")}");
                            exitCode = 11;
                            return exitCode;
                        }
                        continue;
                    }
                    finalCaps = settled;
                    Console.WriteLine($"POC-MONITOR scale settled: current={finalCaps.CurrentPercent}% source={scaleSource}");
                    break;
                }
                finalCaps = now;
            }

            // The readback above is intentionally duplicated after a settle delay. A successful
            // DisplayConfigSetDeviceInfo return is not proof; the actual value must survive.
            await Task.Delay(1500, ct).ConfigureAwait(false);
            var settledCaps = backend.ReadScaleCaps(monitor);
            if (settledCaps is null || settledCaps.CurrentPercent != desiredScale)
            {
                failure = Failure("SCALE_REAPPLY_FAILED",
                    $"Final scale readback is {(settledCaps is null ? "unavailable" : settledCaps.CurrentPercent + "%")}, " +
                    $"expected {desiredScale}%.");
                exitCode = 11;
                return exitCode;
            }
            finalCaps = settledCaps;
            var density = TryReadDensity(backend, monitor);
            var readyElapsed = watch.Elapsed;
            readyAt = readyElapsed;
            var identityStable = edid.SerialMatch;
            var identityVerdict = identityStable
                ? "PER_CLIENT_MONITOR_IDENTITY_STABLE"
                : "IDENTITY_BLOCKED_EDID_SERIAL_NOT_VISIBLE";

            var profileNow = new ClientProfile
            {
                ClientId = clientId,
                StableSerial = stableSerial,
                PreferredScalePercent = desiredScale,
                AttachCount = attachCount + 1,
                LastGdiName = monitor.GdiName,
                LastDeviceInstance = edid.Instance,
                LastContainerId = edid.ContainerId,
                LastEdidSerial = edid.SerialHex,
                LastAttachUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            ClientProfile.Save(profilePath, profileNow);

            ready = new JsonObject
            {
                ["schema"] = Schema,
                ["clientId"] = clientId,
                ["stableSerial"] = stableSerial,
                ["serialHex"] = $"0x{stableSerial:X8}",
                ["desiredScalePercent"] = desiredScale,
                ["monitor"] = new JsonObject
                {
                    ["gdiName"] = monitor.GdiName,
                    ["description"] = monitor.Description,
                    ["left"] = monitor.Bounds.X,
                    ["top"] = monitor.Bounds.Y,
                    ["width"] = monitor.Bounds.Width,
                    ["height"] = monitor.Bounds.Height,
                    ["refreshHz"] = refresh,
                },
                ["scale"] = new JsonObject
                {
                    ["currentBeforePercent"] = currentBefore,
                    ["currentAfterPercent"] = finalCaps.CurrentPercent,
                    ["recommendedPercent"] = recommended,
                    ["minPercent"] = min,
                    ["maxPercent"] = max,
                    ["desiredPercent"] = desiredScale,
                    ["source"] = scaleSource,
                    ["setAttempts"] = setAttempts,
                    ["setDetails"] = setDetails,
                    ["simulatedInitialScale"] = simulateInitial,
                    ["simulatedResetAttempted"] = simulatedResetAttempted,
                    ["simulatedResetApplied"] = simulatedResetApplied,
                    ["initial100Observed"] = initial100Observed,
                    ["reapplied"] = enforced,
                },
                ["density"] = new JsonObject
                {
                    ["helperMonitorDpiX"] = density?.MonitorDpiX,
                    ["helperMonitorDpiY"] = density?.MonitorDpiY,
                    ["note"] = "GetDpiForMonitor is preliminary; the native PMv2 GetDpiForWindow gate is in the C++ POC.",
                },
                ["identity"] = new JsonObject
                {
                    ["stableSerial"] = stableSerial,
                    ["serialHex"] = $"0x{stableSerial:X8}",
                    ["profileAttachCountBefore"] = attachCount,
                    ["edidDeviceId"] = deviceId,
                    ["edidInstance"] = edid.Instance,
                    ["edidContainerId"] = edid.ContainerId,
                    ["edidContainerExpected"] = edid.ExpectedContainerId,
                    ["edidSerial"] = edid.SerialHex,
                    ["edidManufacturer"] = edid.Manufacturer,
                    ["edidProduct"] = edid.Product,
                    ["edidSerialMatch"] = edid.SerialMatch,
                    ["containerIdMatch"] = edid.ContainerMatch,
                    ["stable"] = identityStable,
                    ["verdict"] = identityVerdict,
                    ["detail"] = edid.Detail,
                    ["target"] = target is null ? null : new JsonObject
                    {
                        ["adapterHigh"] = target.AdapterId.HighPart,
                        ["adapterLow"] = target.AdapterId.LowPart,
                        ["sourceId"] = target.SourceId,
                        ["targetId"] = target.TargetId,
                        ["friendlyName"] = target.FriendlyName,
                        ["gdiName"] = target.GdiName,
                        ["devicePath"] = target.DevicePath,
                        ["detail"] = targetDetail,
                    },
                },
                ["driverBefore"] = DriverJson(healthBefore),
                ["physicalMonitorsBefore"] = PhysicalJson(physicalBefore),
                ["timing"] = new JsonObject
                {
                    ["attachToScaleReadyMs"] = (long)readyElapsed.TotalMilliseconds,
                    ["attachToReadyMs"] = (long)watch.Elapsed.TotalMilliseconds,
                },
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["verdicts"] = new JsonArray
                {
                    $"SCALE_{desiredScale}_READBACK_VERIFIED",
                    identityVerdict,
                },
            };
            WriteJson(readyJson, ready);
            WriteKv(readyKv, ready);
            Console.WriteLine($"POC-MONITOR READY: {monitor.GdiName} {monitor.Bounds}, scale={finalCaps.CurrentPercent}%, " +
                $"source={scaleSource}, identity={identityVerdict}; holding until release ({releaseFlag})");
            Console.Out.Flush();

            var holdWatch = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var h = backend.ReadDriverHealth();
                if (!h.Reachable || !h.MonitorArrived)
                {
                    holdReason = "monitor_departed";
                    held = holdWatch.Elapsed;
                    failure = Failure("HOLD_MONITOR_DEPARTED",
                        $"The driver lost the virtual monitor during the hold (reachable={h.Reachable}, arrived={h.MonitorArrived}).");
                    exitCode = 13;
                    break;
                }
                if (WaitForReleaseTimeout(releaseFlag, TimeSpan.Zero))
                {
                    holdReason = "released";
                    held = holdWatch.Elapsed;
                    break;
                }
                if (holdWatch.Elapsed > timeout)
                {
                    holdReason = "timeout";
                    held = holdWatch.Elapsed;
                    failure = Failure("HOLD_TIMEOUT",
                        $"No release flag appeared within {timeout.TotalSeconds:0}s; removing the monitor.");
                    exitCode = 13;
                    break;
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            Console.WriteLine($"POC-MONITOR hold ended: reason={holdReason} held={held.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException)
        {
            failure = Failure("CANCELLED", "The monitor host was cancelled.");
            exitCode = 130;
        }
        catch (Exception ex)
        {
            failure = Failure("MONITOR_HOST_FAILED", ex.Message);
            Console.WriteLine("POC-MONITOR ERROR: " + ex);
            exitCode = 12;
        }
        finally
        {
            try
            {
                await backend.RemoveMonitorAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("POC-MONITOR teardown warning: " + ex.Message);
            }
            var afterHealth = backend.ReadDriverHealth();
            var wait = Stopwatch.StartNew();
            while (afterHealth.MonitorArrived && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                afterHealth = backend.ReadDriverHealth();
            }
            await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            var physicalAfter = guard.Snapshot();
            var physicalReport = DisplayGuardLogic.Compare(physicalBefore, physicalAfter);
            var removed = !afterHealth.MonitorArrived;
            if (!removed)
            {
                failure ??= Failure("CLEANUP_MONITOR_STILL_ARRIVED",
                    "driver-status still reports monitor arrived 1 after teardown.");
                exitCode = exitCode == 0 ? 14 : exitCode;
            }
            if (!physicalReport.Clean)
            {
                failure ??= Failure("PHYSICAL_DISPLAY_DRIFT",
                    string.Join("; ", physicalReport.Drift));
                exitCode = exitCode == 0 ? 14 : exitCode;
            }

            result = ready is not null
                ? (JsonObject)ready.DeepClone()
                : new JsonObject { ["schema"] = Schema, ["clientId"] = clientId };
            result["clientId"] = clientId;
            result["hold"] = new JsonObject
            {
                ["reason"] = holdReason,
                ["heldMs"] = (long)held.TotalMilliseconds,
                ["timeoutSec"] = (long)timeout.TotalSeconds,
            };
            var timing = new JsonObject
            {
                ["attachToReadyMs"] = ready is not null ? (long)readyAt.TotalMilliseconds : null,
                ["attachToCleanupMs"] = (long)watch.Elapsed.TotalMilliseconds,
            };
            if (ready?["timing"] is JsonObject readyTiming)
                timing["attachToScaleReadyMs"] = readyTiming["attachToScaleReadyMs"]?.DeepClone();
            result["timing"] = timing;
            result["cleanup"] = new JsonObject
            {
                ["monitorArrived"] = afterHealth.MonitorArrived,
                ["monitorRemoved"] = removed,
                ["physicalUnchanged"] = physicalReport.Clean,
                ["physicalDrift"] = new JsonArray(physicalReport.Drift.Select(d => (JsonNode?)d).ToArray()),
                ["driverAfter"] = DriverJson(afterHealth),
                ["physicalMonitorsAfter"] = PhysicalJson(physicalAfter),
            };
            if (removed && physicalReport.Clean)
            {
                var verdicts = result["verdicts"] as JsonArray ?? new JsonArray();
                verdicts.Add("PHYSICAL_DISPLAYS_UNCHANGED_AND_FOCUSIDD_REMOVED");
                result["verdicts"] = verdicts;
            }
            if (failure is not null)
            {
                result["failure"] = failure;
                result["overall"] = "BLOCKED";
                WriteJson(failureJson, failure);
            }
            else
            {
                result["overall"] = "PASS_COMPONENT_MONITOR_IDENTITY_SCALE";
            }
            WriteJson(resultJson, result);
            Console.WriteLine($"POC-MONITOR cleanup: removed={removed} arrived={afterHealth.MonitorArrived} " +
                $"physicalUnchanged={physicalReport.Clean} exit={exitCode}");
            Console.Out.Flush();
        }
        return exitCode;
    }

    private static async Task<bool> WaitForScaleAsync(WindowsDisplayBackend backend, MonitorInfo monitor,
        int percent, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var caps = backend.ReadScaleCaps(monitor);
            if (caps?.CurrentPercent == percent) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static DensityReadout? TryReadDensity(WindowsDisplayBackend backend, MonitorInfo monitor)
    {
        try { return backend.ReadDensity(monitor); }
        catch (Exception ex)
        {
            Console.WriteLine("POC-MONITOR density readback unavailable: " + ex.Message);
            return null;
        }
    }

    private static PhoneTarget? TryFindTarget(DisplayTopologyBefore before, int width, int height, out string detail)
    {
        try
        {
            var target = DisplayConfigApi.FindCreatedPhoneMonitor(before, width, height, out detail);
            return target;
        }
        catch (Exception ex)
        {
            detail = "target identification failed: " + ex.Message;
            return null;
        }
    }

    private static bool WaitForReleaseTimeout(string releaseFlag, TimeSpan _) => File.Exists(releaseFlag);

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string ProfileName(string clientId)
    {
        var readable = new string(clientId.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '_').ToArray());
        if (readable.Length == 0) readable = "client";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..12].ToLowerInvariant();
        return $"{readable}-{hash}";
    }

    private static JsonObject Failure(string code, string detail) => new()
    {
        ["schema"] = Schema,
        ["failure"] = new JsonObject { ["code"] = code, ["detail"] = detail },
        ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    };

    private static JsonObject DriverJson(DriverHealth h) => new()
    {
        ["reachable"] = h.Reachable,
        ["protocolVersion"] = h.ProtocolVersion,
        ["monitorArrived"] = h.MonitorArrived,
        ["width"] = h.Width,
        ["height"] = h.Height,
        ["refreshHz"] = h.RefreshHz,
        ["framesPublished"] = h.FramesPublished,
        ["framesDropped"] = h.FramesDropped,
        ["lastError"] = $"0x{h.LastError:X8}",
        ["surfaceWin32Error"] = h.SurfaceWin32Error,
    };

    private static JsonArray PhysicalJson(DisplaySnapshot snapshot)
    {
        var array = new JsonArray();
        foreach (var m in snapshot.Monitors)
        {
            array.Add(new JsonObject
            {
                ["deviceName"] = m.DeviceName,
                ["left"] = m.Bounds.X,
                ["top"] = m.Bounds.Y,
                ["width"] = m.Bounds.Width,
                ["height"] = m.Bounds.Height,
                ["dpi"] = m.Dpi,
                ["primary"] = m.Primary,
            });
        }
        return array;
    }

    private static void WriteKv(string path, JsonObject ready)
    {
        var monitor = ready["monitor"] as JsonObject ?? new JsonObject();
        var scale = ready["scale"] as JsonObject ?? new JsonObject();
        var identity = ready["identity"] as JsonObject ?? new JsonObject();
        var density = ready["density"] as JsonObject ?? new JsonObject();
        var lines = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema"] = Schema,
            ["client_id"] = Str(ready["clientId"]),
            ["stable_serial"] = Str(ready["stableSerial"]),
            ["serial_hex"] = Str(ready["serialHex"]),
            ["gdi_name"] = Str(monitor["gdiName"]),
            ["left"] = Str(monitor["left"]),
            ["top"] = Str(monitor["top"]),
            ["width"] = Str(monitor["width"]),
            ["height"] = Str(monitor["height"]),
            ["refresh_hz"] = Str(monitor["refreshHz"]),
            ["desired_scale"] = Str(scale["desiredPercent"]),
            ["current_scale_before"] = Str(scale["currentBeforePercent"]),
            ["current_scale"] = Str(scale["currentAfterPercent"]),
            ["recommended_scale"] = Str(scale["recommendedPercent"]),
            ["min_scale"] = Str(scale["minPercent"]),
            ["max_scale"] = Str(scale["maxPercent"]),
            ["scale_source"] = Str(scale["source"]),
            ["scale_reapplied"] = Str(scale["reapplied"]),
            ["simulated_reset_applied"] = Str(scale["simulatedResetApplied"]),
            ["initial_100_observed"] = Str(scale["initial100Observed"]),
            ["monitor_dpi_x"] = Str(density["helperMonitorDpiX"]),
            ["monitor_dpi_y"] = Str(density["helperMonitorDpiY"]),
            ["identity_stable"] = Str(identity["stable"]),
            ["identity_verdict"] = Str(identity["verdict"]),
            ["edid_serial"] = Str(identity["edidSerial"]),
            ["edid_serial_match"] = Str(identity["edidSerialMatch"]),
            ["edid_instance"] = Str(identity["edidInstance"]),
            ["edid_container_id"] = Str(identity["edidContainerId"]),
            ["edid_container_expected"] = Str(identity["edidContainerExpected"]),
            ["edid_container_match"] = Str(identity["containerIdMatch"]),
            ["edid_device_id"] = Str(identity["edidDeviceId"]),
            ["ready_utc"] = Str(ready["createdUtc"]),
        };
        var text = string.Join("\n", lines.Select(kv => $"{kv.Key}={KvEscape(kv.Value)}")) + "\n";
        WriteAtomic(path, text);
    }

    private static string Str(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString(); }
        catch { return node.ToJsonString(); }
    }

    private static string KvEscape(string value) =>
        value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();

    private static void WriteJson(string path, JsonNode node) =>
        WriteAtomic(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }

    private static int Int(string[] args, string name, int fallback) =>
        int.TryParse(Opt(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static int? OptInt(string[] args, string name) =>
        int.TryParse(Opt(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private sealed class ClientProfile
    {
        public string ClientId { get; set; } = "";
        public uint StableSerial { get; set; }
        public int PreferredScalePercent { get; set; } = 350;
        public int AttachCount { get; set; }
        public string? LastGdiName { get; set; }
        public string? LastDeviceInstance { get; set; }
        public string? LastContainerId { get; set; }
        public string? LastEdidSerial { get; set; }
        public string LastAttachUtc { get; set; } = "";

        public static ClientProfile? Load(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(path))
                    : null;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"The per-client profile at {path} is unreadable: {ex.Message}", ex);
            }
        }

        public static void Save(string path, ClientProfile profile)
        {
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private sealed record EdidIdentity(
        bool Found, string? DeviceId, string? Instance, string? ContainerId, string? ExpectedContainerId,
        string? SerialHex, bool SerialMatch, bool ContainerMatch, string? Manufacturer, int? Product, string Detail);

    private static class DisplayIdentity
    {
        private const string MonitorClass = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum,
            ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        public static string? ActiveMonitorDeviceId(string gdiName)
        {
            try
            {
                var dd = new DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
                    DeviceName = "",
                    DeviceString = "",
                    DeviceID = "",
                    DeviceKey = "",
                };
                return EnumDisplayDevicesW(gdiName, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceID)
                    ? dd.DeviceID
                    : null;
            }
            catch { return null; }
        }

        public static EdidIdentity Read(uint stableSerial, string? activeDeviceId)
        {
            var details = new List<string>();
            var activeInstance = InstanceFromDeviceId(activeDeviceId);
            if (activeInstance is not null) details.Add($"active instance '{activeInstance}'");
            var candidates = new List<EdidIdentity>();
            try
            {
                using var displayKey = Registry.LocalMachine.OpenSubKey(MonitorClass);
                var vendors = displayKey?.GetSubKeyNames() ?? Array.Empty<string>();
                foreach (var vendor in vendors)
                {
                    if (!vendor.Equals("FOC0046", StringComparison.OrdinalIgnoreCase) &&
                        !vendor.StartsWith("FOC", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var vendorKey = displayKey!.OpenSubKey(vendor);
                    if (vendorKey is null) continue;
                    foreach (var instance in vendorKey.GetSubKeyNames())
                    {
                        var parsed = ReadInstance(vendor, instance, stableSerial);
                        if (parsed is not null) candidates.Add(parsed);
                    }
                }
            }
            catch (Exception ex)
            {
                details.Add("registry read failed: " + ex.Message);
            }
            if (candidates.Count == 0)
            {
                return new EdidIdentity(false, activeDeviceId, activeInstance, null, null, null, false, false, null, null,
                    "No FOC0046 EDID was readable from HKLM\\SYSTEM\\CurrentControlSet\\Enum\\DISPLAY; " +
                    string.Join("; ", details));
            }
            var active = candidates.FirstOrDefault(c => string.Equals(c.Instance, activeInstance, StringComparison.OrdinalIgnoreCase));
            var matching = candidates.Where(c => c.SerialMatch).ToList();
            var picked = active ?? matching.FirstOrDefault() ?? candidates[0];
            details.Add($"{candidates.Count} FocusIdd EDID instance(s) visible, {matching.Count} with serial match");
            if (active is null) details.Add("active instance was not correlated; using EDID serial match");
            return picked with { Detail = string.Join("; ", details) };
        }

        private static EdidIdentity? ReadInstance(string vendor, string instance, uint stableSerial)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{MonitorClass}\{vendor}\{instance}");
            if (key is null) return null;
            var container = key.GetValue("ContainerID") as string;
            byte[]? edid = null;
            using (var parameters = key.OpenSubKey("Device Parameters"))
                edid = parameters?.GetValue("EDID", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
            if (edid is null || edid.Length < 16) return null;
            var manufacturer = DecodeManufacturer(edid);
            var product = edid[10] | (edid[11] << 8);
            var serial = BitConverter.ToUInt32(edid, 12);
            var expected = ExpectedContainer(serial);
            var containerMatch = string.Equals(container?.Trim('{', '}'), expected.ToString("D"),
                StringComparison.OrdinalIgnoreCase);
            return new EdidIdentity(
                true,
                $"MONITOR\\{vendor}\\{instance}",
                instance,
                container,
                expected.ToString("D"),
                $"0x{serial:X8}",
                serial == stableSerial,
                containerMatch,
                manufacturer,
                product,
                $"vendor={vendor} product=0x{product:X4} serial=0x{serial:X8} " +
                $"containerMatch={containerMatch} serialMatch={serial == stableSerial}");
        }

        private static string? InstanceFromDeviceId(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            var parts = deviceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[^1] : null;
        }

        private static string DecodeManufacturer(byte[] edid)
        {
            var value = (edid[8] << 8) | edid[9];
            var a = (char)(((value >> 10) & 0x1F) + 'A' - 1);
            var b = (char)(((value >> 5) & 0x1F) + 'A' - 1);
            var c = (char)((value & 0x1F) + 'A' - 1);
            return $"{a}{b}{c}";
        }

        private static Guid ExpectedContainer(uint serial) => new(
            0xF0C05500, 0xA11C, 0x4D3A,
            0x9E, 0x1B, 0, 0,
            (byte)(serial & 0xFF),
            (byte)((serial >> 8) & 0xFF),
            (byte)((serial >> 16) & 0xFF),
            (byte)((serial >> 24) & 0xFF));
    }
}
