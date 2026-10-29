using System.Runtime.Versioning;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

[SupportedOSPlatform("windows")]
internal static class WindowsProbeRundown
{
    internal static async Task TryObserveStopped(OwnerRunContext context, RunCoordinator owner)
    {
        var state = owner.Snapshot();
        if (state.Retired || state.ProbeStopped || state.Probe is null) return;
        if (!state.Closing) throw new InvalidOperationException("Probe rundown requires the Closing fence.");
        try
        {
            using var monitor = WorkerProcessMonitor.OpenProbe(state.Probe,
                Path.Combine(context.DirectoryPath, "bundle", OwnerRuntimeContract.ProbeImage));
            var release = context.RunRelative + "/release-monitor.flag";
            try
            {
                var bytes = ContractJson.Write(new { version = 1, runId = state.RunId });
                using var input = new MemoryStream(bytes, false);
                using var file = context.Root.CopyProtectedFile(release, input, ContractJson.Hash(bytes), 4096);
            }
            catch (IOException) when (File.Exists(SecureMaintenanceRoot.ResolveRelative(context.Root.DirectoryPath, release)))
            { using var existing = context.Root.OpenProtectedRead(release); } // request only, never completion
            var clock = new WindowsDriverTestClock();
            var until = checked(clock.Milliseconds + ContractJson.Read<SafetyPolicy>(context.PolicyBytes).HeartbeatTimeoutMs);
            while (!monitor.HasExited() && clock.Milliseconds < until) await Task.Delay(100).ConfigureAwait(false);
            if (!monitor.HasExited())
            {
                monitor.StopProbe();
                // Successful termination request is not exit. Give the retained
                // object a bounded observation opportunity; unknown stays unknown.
                until = checked(clock.Milliseconds + 1000);
                while (!monitor.HasExited() && clock.Milliseconds < until) await Task.Delay(100).ConfigureAwait(false);
            }
            if (monitor.HasExited() && !owner.Snapshot().Retired && !owner.Snapshot().ProbeStopped)
                owner.ObserveProbeStopped(monitor.Identity);
        }
        catch (Exception error)
        {
            // Missing/reused PID or lost observation never means stopped. Exact
            // best-effort device removal may still run; retirement remains fenced.
            Console.Error.WriteLine("Probe rundown remains unconfirmed: " + error.Message);
        }
    }
}
