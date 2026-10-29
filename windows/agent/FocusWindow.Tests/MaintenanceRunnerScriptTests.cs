using System.Diagnostics;
using System.Text.Json;
using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

// Windows PowerShell 5.1 runs the shipping UI event handlers with mocked UI,
// process, clock and Scheduler boundaries. Fixture statuses come from the real
// journal/coordinator; neither a driver nor a real desktop window is touched.
public sealed class MaintenanceRunnerScriptTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "deskfuse-script-" + Guid.NewGuid().ToString("N"));
    private sealed class Clock : IDriverTestClock { public long Milliseconds { get; set; } = 1000; }
    private sealed class Verifier : IPackageAdmissionVerifier
    {
        public IPackageAdmissionLease AcquireForCreation(RunManifest manifest, NativeOperation? operation) => new Lease();
        private sealed class Lease : IPackageAdmissionLease { public T CommitAdmission<T>(Func<T> commit) => commit(); public void Dispose() { } }
    }
    private sealed class Native(RunManifest manifest) : IDriverNativeOperations
    {
        public NativeResult Invoke(NativeOperation operation) => operation == NativeOperation.RemoveDevice
            ? new(true, ParentAndSubtreeAbsent: true, EnumerationSucceeded: true) : new(true);
        public AbsenceObservation ObserveAbsence() => new(manifest.RunId, manifest.InstanceId, true, true);
    }
    private sealed record Frame(long Now, bool Click, bool Cancel, bool FailRead, WatchStatus Status);

    [Theory]
    [InlineData("watch")]
    [InlineData("recover")]
    [InlineData("rollback")]
    [InlineData("safe")]
    [InlineData("legacy")]
    [InlineData("risk")]
    [InlineData("describe")]
    public async Task Prepared_entrypoints_dispatch_only_the_bound_role_or_reject_before_runtime(string role)
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-File",
            Path.Combine(AppContext.BaseDirectory, "maintenance", "test-maintenance-entrypoints.ps1"), "-Case", role }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        var output = await stdout; var error = await stderr;
        Assert.True(process.ExitCode == 0, output + error);
        Assert.Contains("PASS: prepared entrypoint " + role, output);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("lost-watch")]
    [InlineData("read-failure")]
    [InlineData("wrong-run")]
    [InlineData("missing-first")]
    [InlineData("missing-second")]
    [InlineData("missing-third")]
    public async Task Shipping_timer_and_dialog_handlers_preserve_the_three_checkpoint_contract(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid(); var clock = new Clock();
        var policy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-test-policy.json"));
        var hash = new string('a', 64);
        var manifest = new RunManifest(2, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            ContractJson.Hash(policy), new() { ["helper.exe"] = hash }, RunManifest.WatchName(id), RunManifest.BootName(id),
            new(PackageProfile.LocalTest, Guid.NewGuid(), hash, hash, hash));
        var runtime = new OwnerRuntimeContract(1, id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(100, 1000, Guid.NewGuid(), WorkerRole.Runner), 1440, 3120, 3.5, 170, 60);
        var owner = new RunCoordinator(RunJournal.Create(Path.Combine(directory, id.ToString("N")), manifest), policy, clock, new Verifier());
        var watch = new WorkerIdentity(101, 1001, runtime.WatchNonce, WorkerRole.Watch);
        var creator = new WorkerIdentity(102, 1002, runtime.CreatorNonce, WorkerRole.Creator);
        var probe = new WorkerIdentity(12345, 123456, runtime.ProbeNonce, WorkerRole.Probe);
        var recovery = new WorkerIdentity(104, 1004, runtime.BootNonce, WorkerRole.Recovery);
        owner.Arm(new(id, watch, manifest.BootTask, true, true));
        var native = new Native(manifest);
        new RunProvisioner(owner, native).Run(creator, CancellationToken.None); owner.ObserveExitedWorker(creator);
        var frames = new List<Frame>(); long revision = 0;
        WatchStatus Status(long now) => new(1, id, watch, ++revision, now, false, owner.Snapshot());
        frames.Add(new(2000, false, false, false, Status(2000))); // UI launches the parked probe once
        owner.RegisterProbe(probe); owner.MarkProofReady();
        var ready = Status(3000);
        frames.Add(new(3000, false, scenario == "cancel", false, ready));
        if (scenario == "complete")
        {
            foreach (var (step, now) in new[] { (1, 61000), (2, 181000), (3, 301000) })
            {
                clock.Milliseconds = now;
                frames.Add(new(now, true, false, false, Status(now)));
                Assert.True(owner.Acknowledge(id, step));
            }
            owner.ObserveProbeStopped(probe);
            Assert.True(new RunRecovery(owner, native).RunOnce(recovery, "ProofFinished").Retired);
            frames.Add(new(303000, false, false, false, Status(303000)));
        }
        else if (scenario == "cancel")
        {
            owner.Close("OperatorClosed"); owner.ObserveProbeStopped(probe);
            Assert.True(new RunRecovery(owner, native).RunOnce(recovery, "OperatorClosed").Retired);
            frames.Add(new(4000, false, false, false, Status(4000)));
        }
        else if (scenario.StartsWith("missing-", StringComparison.Ordinal))
        {
            var missing = scenario == "missing-first" ? 1 : scenario == "missing-second" ? 2 : 3;
            for (var step = 1; step < missing; step++)
            {
                var now = step == 1 ? 61000 : 181000;
                clock.Milliseconds = now;
                frames.Add(new(now, true, false, false, Status(now)));
                Assert.True(owner.Acknowledge(id, step));
            }
            clock.Milliseconds = missing == 1 ? 86000 : missing == 2 ? 206000 : 326000;
            owner.CheckHealth(clock.Milliseconds, clock.Milliseconds);
            Assert.True(owner.Snapshot().Closing);
            frames.Add(new(clock.Milliseconds, false, false, false, Status(clock.Milliseconds)));
            owner.ObserveProbeStopped(probe);
            Assert.True(new RunRecovery(owner, native).RunOnce(recovery, "MissingCheckpoint").Retired);
            frames.Add(new(clock.Milliseconds + 1000, false, false, false, Status(clock.Milliseconds + 1000)));
        }
        else
        {
            var fault = scenario == "wrong-run" ? ready with { RunId = Guid.NewGuid() } : ready;
            frames.Add(new(14000, false, false, scenario == "read-failure", fault));
            frames.Add(new(351001, false, false, scenario == "read-failure", fault));
        }
        var fixture = Path.Combine(directory, "fixture.json");
        File.WriteAllBytes(fixture, ContractJson.Write(new { runtime, policy = ContractJson.Read<SafetyPolicy>(policy), namespaceId = Guid.NewGuid(), startAt = 2000, frames }));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-File",
            Path.Combine(AppContext.BaseDirectory, "maintenance", "test-maintenance-runner.ps1"), "-FixturePath", fixture }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(); throw; } // this exact test-owned harness only
        var output = await stdout; var error = await stderr;
        Assert.True(process.ExitCode == 0, output + error);
        using var result = JsonDocument.Parse(output);
        var value = result.RootElement;
        Assert.Equal(1, value.GetProperty("probeStarts").GetInt32());
        var tasks = value.GetProperty("tasks").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
        Assert.False(tasks[0]); Assert.Equal(1, tasks.Count(x => !x));
        if (scenario == "complete")
        {
            Assert.True(value.GetProperty("passed").GetBoolean(), output + error);
            Assert.True(value.GetProperty("finished").GetBoolean());
            Assert.DoesNotContain(true, tasks);
            Assert.Equal(new[] { 0, 1, 2, 3 }, value.GetProperty("writes").EnumerateArray().Select(x => x.GetProperty("acknowledge").GetInt32()).Distinct());
        }
        else
        {
            Assert.False(value.GetProperty("passed").GetBoolean());
            Assert.True(value.GetProperty("close").GetBoolean());
            Assert.True(tasks.Count(x => x) <= 2); // request + final idempotent nudge, never unbounded launches
        }
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
