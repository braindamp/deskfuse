using System.Diagnostics;
using System.Runtime.Versioning;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

/// <summary>Responsive control owner; every potentially blocking PnP call runs in a separate, exact worker.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsWatchRole
{
    internal static async Task<int> Run(OwnerRunContext context, OwnerCommand command)
    {
        var runtime = context.Runtime();
        if (command.Nonce != runtime.WatchNonce) throw new InvalidDataException("Wrong watchdog launch identity.");
        var owner = context.Coordinator(false);
        var self = Program.CurrentWorker(runtime.WatchNonce, WorkerRole.Watch);
        var initial = owner.Snapshot();
        if (initial.Armed || initial.Closing || initial.Retired)
            throw new InvalidOperationException("This run cannot arm a replacement watchdog or replay provisioning.");
        using var runner = WorkerProcessMonitor.OpenExact(runtime.Runner);
        if (runner.HasExited()) throw new InvalidOperationException("The prepared runner has exited.");
        // Validate private mailbox custody before using its stable file as a lock.
        using (var mailbox = context.Root.OpenProtectedRead(context.RunRelative + "/" + RunnerControlMailbox.FileName))
            if (mailbox.Length is <= 0 or > RunnerControlMailbox.MaximumBytes) throw new InvalidDataException("Invalid prepared mailbox.");
        using (var status = context.Root.OpenProtectedRead(context.RunRelative + "/" + WatchStatus.FileName)) { }
        var control = RunnerControlMailbox.Read(context.DirectoryPath);
        control.Validate(runtime);
        if (control.Close) { owner.Close("RunnerClosedBeforeArming"); return 3; }
        WindowsRecoveryTasks.Verify(context, runtime);
        WriteNew(context, "watch.json", self);
        owner.Arm(new(context.Manifest.RunId, self, context.Manifest.BootTask, true, true));
        var clock = new WindowsDriverTestClock();
        var start = owner.Snapshot().ArmedAtMs!.Value;
        var progress = new RunnerProgress(runtime, start);
        var policy = ContractJson.Read<SafetyPolicy>(context.PolicyBytes);
        Process? creator = null, recovery = null;
        WorkerProcessMonitor? creatorMonitor = null, recoveryMonitor = null, probe = null;
        var creatorObserved = false;
        var recoveryObserved = false;
        long? closingAt = null;
        var releaseWritten = false;
        var recoverySchedule = new RecoveryAttemptSchedule();
        long statusRevision = 0, lastStatusAt = -1;
        try
        {
            try
            {
                (creator, creatorMonitor) = Launch(context, runtime.Provision(command.NamespaceId), WorkerRole.Creator);
            }
            catch (Exception error)
            {
                owner.Close("CreatorLaunchFailed");
                Console.Error.WriteLine(error.Message);
            }
            while (clock.Milliseconds - start < policy.AutomaticRetryWindowMs)
            {
                var now = clock.Milliseconds;
                var advanced = false;
                if (!owner.Snapshot().Closing)
                {
                    try
                    {
                        control = RunnerControlMailbox.Read(context.DirectoryPath);
                        advanced = progress.Observe(control, now);
                    }
                    catch (Exception error) { owner.Close("RunnerControlFailed"); Console.Error.WriteLine(error.Message); }
                }
                try
                {
                    if (runner.HasExited() || control.Close) owner.Close("RunnerStopped");
                    if (creatorMonitor is not null && !creatorObserved && creatorMonitor.HasExited())
                    {
                        var state = owner.Snapshot();
                        if (state.Creator == creatorMonitor.Identity) owner.ObserveExitedWorker(creatorMonitor.Identity);
                        else owner.Close("CreatorExitedBeforeRegistration");
                        creatorObserved = true;
                        if (!owner.Snapshot().Closing && owner.Snapshot().Permit != CreatorPermit.Relinquished)
                            owner.Close("CreatorDidNotRelinquish");
                    }
                    if (recoveryMonitor is not null && !recoveryObserved && recoveryMonitor.HasExited())
                    {
                        var state = owner.Snapshot();
                        if (state.Pending.Any(p => p.Worker == recoveryMonitor.Identity)) owner.ObserveExitedWorker(recoveryMonitor.Identity);
                        recoveryObserved = true;
                    }
                    var snapshot = owner.Snapshot();
                    if (!snapshot.Closing && snapshot.CreatorExited && snapshot.Permit == CreatorPermit.Relinquished &&
                        probe is null && control.Probe is { } identity)
                    {
                        context.HoldProbeCode();
                        probe = WorkerProcessMonitor.OpenProbe(identity,
                            Path.Combine(context.DirectoryPath, "bundle", OwnerRuntimeContract.ProbeImage));
                        if (probe.HasExited()) throw new InvalidOperationException("The parked probe exited before registration.");
                        owner.RegisterProbe(identity);
                        // Closing after registration is safe: this exact process
                        // remains retained and must stop before retirement.
                        if (!owner.Snapshot().Closing)
                            WriteNew(context, ProbeStartAuthorization.FileName,
                                new ProbeStartAuthorization(1, command.RunId, identity.Nonce, identity.Pid, identity.CreationFileTime));
                    }
                    if (probe is not null && probe.HasExited() && !owner.Snapshot().ProbeStopped)
                        owner.ObserveExitedWorker(probe.Identity);
                    snapshot = owner.Snapshot();
                    if (advanced && !snapshot.Closing && control.ProofReady && !snapshot.ProofReady && probe is not null)
                    {
                        using var held = context.Root.OpenProtectedRead(context.RunRelative + "/monitor-held.flag");
                        if (held.Length is <= 0 or > 4096 || probe.HasExited())
                            throw new InvalidDataException("The approved probe has not established its hold.");
                        owner.MarkProofReady();
                    }
                    snapshot = owner.Snapshot();
                    if (advanced && !snapshot.Closing && control.Acknowledge > snapshot.Acknowledged.Length)
                        owner.Acknowledge(command.RunId, control.Acknowledge);
                }
                catch (Exception error)
                {
                    owner.Close("WatchObservationFailed");
                    Console.Error.WriteLine(error.Message);
                }
                owner.CheckHealth(progress.LastHeartbeatMs, now);
                var current = owner.Snapshot();
                if (lastStatusAt < 0 || now - lastStatusAt >= 1000 || current.Retired)
                {
                    new WatchStatus(1, command.RunId, self, ++statusRevision, now,
                        now - start >= policy.CleanupObservationDeadlineMs, current).Write(context.DirectoryPath);
                    lastStatusAt = now;
                }
                if (current.Retired)
                {
                    WindowsRecoveryTasks.UnregisterRetired(context, runtime);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new OwnerCommandResult(1, OwnerRole.Watch, true, current), ContractJson.Options));
                    return 0;
                }
                if (current.Closing)
                {
                    closingAt ??= now;
                    if (probe is not null && !current.ProbeStopped)
                    {
                        try
                        {
                            if (!releaseWritten) { WriteNew(context, "release-monitor.flag", new { version = 1, runId = command.RunId }); releaseWritten = true; }
                            if (now - closingAt.Value >= policy.HeartbeatTimeoutMs) probe.StopProbe();
                            if (probe.HasExited()) owner.ObserveProbeStopped(probe.Identity);
                        }
                        catch (Exception error) { Console.Error.WriteLine("Probe rundown incomplete: " + error.Message); }
                    }
                    current = owner.Snapshot();
                    if (recoverySchedule.ShouldStart(current, recoveryMonitor is null || recoveryObserved, now))
                    {
                        recoveryMonitor?.Dispose(); recoveryMonitor = null;
                        recovery?.Dispose(); recovery = null;
                        recoverySchedule.Starting(current, now);
                        try
                        {
                            (recovery, recoveryMonitor) = Launch(context,
                                new(OwnerRole.Recover, command.NamespaceId, command.RunId, Guid.NewGuid()), WorkerRole.Recovery);
                            recoveryObserved = false;
                        }
                        catch (Exception error) { Console.Error.WriteLine("Scoped recovery launch failed: " + error.Message); }
                    }
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            // A bounded observation/retry window is not a native-call timeout.
            // Exact task identities and unresolved state survive for recovery.
            Console.Error.WriteLine("Recovery observation window ended; retirement was not certified. Recovery tasks retained.");
            return 3;
        }
        finally
        {
            probe?.Dispose(); recoveryMonitor?.Dispose(); creatorMonitor?.Dispose();
            recovery?.Dispose(); creator?.Dispose();
        }
    }

    private static (Process, WorkerProcessMonitor) Launch(OwnerRunContext context, OwnerCommand command, WorkerRole role)
    {
        var start = new ProcessStartInfo(context.HelperImage)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(context.HelperImage)! };
        foreach (var argument in command.Arguments()) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Exact helper process was not created.");
        try
        {
            return (process, WorkerProcessMonitor.FromStartedProcess(process, command.Nonce!.Value, role));
        }
        catch { process.Dispose(); throw; } // Never equate lost observation with a native return.
    }

    private static void WriteNew<T>(OwnerRunContext context, string leaf, T value)
    {
        var bytes = ContractJson.Write(value);
        using var source = new MemoryStream(bytes, writable: false);
        using var target = context.Root.CopyProtectedFile(context.RunRelative + "/" + leaf, source, ContractJson.Hash(bytes), 4096);
    }
}
