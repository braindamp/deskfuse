using System.Diagnostics;
using System.Runtime.Versioning;
using DeskFuse.DriverTestOwner;
using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class WorkerProcessMonitorTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Original_launched_process_object_remains_observable_after_fast_child_exit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { Arguments = "/d /c exit 0", UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(child.WaitForExit(5000));
        using var monitor = WorkerProcessMonitor.FromStartedProcess(child, Guid.NewGuid(), WorkerRole.Recovery);
        Assert.Equal(child.Id, monitor.Identity.Pid);
        Assert.True(monitor.HasExited());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Retained_current_process_is_not_exited_and_reused_pid_is_rejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var current = Process.GetCurrentProcess();
        var identity = new WorkerIdentity(current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Guid.NewGuid(), WorkerRole.Creator);
        using var monitor = WorkerProcessMonitor.OpenExact(identity);
        Assert.Equal(identity, monitor.Identity);
        Assert.False(monitor.HasExited());
        Assert.Throws<InvalidOperationException>(() => monitor.StopProbe());
        Assert.Throws<InvalidOperationException>(() => WorkerProcessMonitor.OpenProbe(identity, current.MainModule!.FileName));
        Assert.Throws<InvalidDataException>(() => WorkerProcessMonitor.OpenProbe(identity with { Role = WorkerRole.Probe }, @"C:\wrong\probe.exe"));
        Assert.Throws<InvalidDataException>(() => WorkerProcessMonitor.OpenExact(identity with
            { CreationFileTime = checked(identity.CreationFileTime + 1) }));
    }

    [Fact]
    public void Driver_test_clock_is_nonnegative_and_monotonic()
    {
        if (!OperatingSystem.IsWindows()) return;
        var clock = new WindowsDriverTestClock();
        var first = clock.Milliseconds;
        Assert.True(first >= 0);
        Assert.True(clock.Milliseconds >= first);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Recovery_adapter_construction_does_not_require_the_candidate_package()
    {
        if (!OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid();
        var manifest = new RunManifest(1, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            new string('a', 64), new() { ["helper.exe"] = new string('b', 64) },
            RunManifest.WatchName(id), RunManifest.BootName(id));
        // Constructor and disposal perform no native device calls. The source
        // package is deliberately absent; exact recovery must not depend on it.
        using var adapter = new WindowsPnpAdapter(manifest, Path.Combine(Path.GetTempPath(), id.ToString("N")));
    }
}
