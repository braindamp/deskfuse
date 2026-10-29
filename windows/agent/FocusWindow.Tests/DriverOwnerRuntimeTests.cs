using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DriverOwnerRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "deskfuse-runtime-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid NamespaceId = Guid.NewGuid(), RunId = Guid.NewGuid();
    private static OwnerRuntimeContract Runtime() => new(1, RunId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), new(1234, 123456, Guid.NewGuid(), WorkerRole.Runner), 1440, 3120, 3.5, 170, 60);
    private static RunnerControl Control(OwnerRuntimeContract runtime, long revision = 1) => new(1, RunId, runtime.Runner.Nonce, revision);

    [Theory]
    [InlineData(OwnerRole.Status)]
    [InlineData(OwnerRole.Control)]
    [InlineData(OwnerRole.Provision)]
    [InlineData(OwnerRole.Watch)]
    [InlineData(OwnerRole.Recover)]
    public void Fixed_roles_round_trip_without_executable_or_device_arguments(OwnerRole role)
    {
        var command = new OwnerCommand(role, NamespaceId, RunId,
            role is OwnerRole.Provision or OwnerRole.Watch or OwnerRole.Recover ? Guid.NewGuid() : null,
            role == OwnerRole.Control ? OwnerControlAction.Acknowledge : null, role == OwnerRole.Control ? 2 : null,
            Boot: role == OwnerRole.Recover);
        Assert.Equal(command, OwnerCommand.Parse(command.Arguments()));
    }

    [Theory]
    [InlineData("--instance", "ROOT\\OTHER\\0")]
    [InlineData("--image", "unapproved.exe")]
    [InlineData("--namespace", "00000000000000000000000000000000")]
    [InlineData("--action", "ack")]
    [InlineData("--nonce", "11111111111111111111111111111111")]
    [InlineData("--boot", "unused")]
    public void Rejects_unknown_inapplicable_and_duplicate_options(string name, string value)
    {
        var args = new OwnerCommand(OwnerRole.Status, NamespaceId, RunId).Arguments().Concat(new[] { name, value }).ToArray();
        Assert.Throws<ArgumentException>(() => OwnerCommand.Parse(args));
    }

    [Fact]
    public void Rejects_missing_nonce_incomplete_ack_and_noncanonical_guid()
    {
        Assert.Throws<ArgumentException>(() => OwnerCommand.Parse(new OwnerCommand(OwnerRole.Provision, NamespaceId, RunId).Arguments()));
        Assert.Throws<ArgumentException>(() => OwnerCommand.Parse(new OwnerCommand(OwnerRole.Control, NamespaceId, RunId,
            Action: OwnerControlAction.Acknowledge).Arguments()));
        Assert.Throws<ArgumentException>(() => OwnerCommand.Parse(["status", "--namespace", NamespaceId.ToString(), "--run", RunId.ToString("N")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_xml_has_only_the_fixed_action_and_expected_trigger(bool boot)
    {
        var runtime = Runtime();
        var spec = new RecoveryTaskContract(boot ? RunManifest.BootName(RunId) : RunManifest.WatchName(RunId),
            @"C:\Program Files\DeskFuse-Maintenance\helper.exe", @"C:\Program Files\DeskFuse-Maintenance",
            boot ? runtime.BootRecovery(NamespaceId) : runtime.Watch(NamespaceId), boot);
        spec.RequireMatches(spec.ToXml());
        Assert.DoesNotContain("<LogonType>ServiceAccount", spec.ToXml());
        var document = XDocument.Parse(spec.ToXml());
        var ns = document.Root!.Name.Namespace;
        document.Descendants(ns + "Command").Single().Value = @"C:\unapproved.exe";
        Assert.Throws<InvalidDataException>(() => spec.RequireMatches(document.ToString()));
    }

    [Theory]
    [InlineData("MultipleInstancesPolicy", "Parallel")]
    [InlineData("Enabled", "false")]
    [InlineData("ExecutionTimeLimit", "PT1M")]
    [InlineData("StopIfGoingOnBatteries", "true")]
    [InlineData("RunOnlyIfNetworkAvailable", "true")]
    [InlineData("WakeToRun", "true")]
    public void Scheduler_conditions_cannot_silently_change_recovery(string setting, string value)
    {
        var spec = new RecoveryTaskContract(RunManifest.WatchName(RunId), @"C:\private\helper.exe", @"C:\private", Runtime().Watch(NamespaceId), false);
        var doc = XDocument.Parse(spec.ToXml());
        var ns = doc.Root!.Name.Namespace;
        doc.Root.Element(ns + "Settings")!.Element(ns + setting)!.Value = value;
        Assert.Throws<InvalidDataException>(() => spec.RequireMatches(doc.ToString()));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_accepts_xml_in_an_unregistered_in_memory_task_definition()
    {
        if (!OperatingSystem.IsWindows()) return;
        object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        object? definition = null;
        try
        {
            // No Connect/RegisterTask/Run calls: this only validates XML using
            // the Windows scheduler parser. No installed task is created.
            definition = ((dynamic)service).NewTask(0);
            foreach (var boot in new[] { false, true })
            {
                var runtime = Runtime();
                var spec = new RecoveryTaskContract(boot ? RunManifest.BootName(RunId) : RunManifest.WatchName(RunId),
                    @"C:\private\helper.exe", @"C:\private", boot ? runtime.BootRecovery(NamespaceId) : runtime.Watch(NamespaceId), boot);
                ((dynamic)definition).XmlText = spec.ToXml();
                spec.RequireMatches((string)((dynamic)definition).XmlText);
            }
        }
        finally
        {
            if (definition is not null) Marshal.FinalReleaseComObject(definition);
            Marshal.FinalReleaseComObject(service);
        }
    }

    [Fact]
    public void Successful_cleanup_is_repeated_only_after_the_actual_creation_frontier_changes()
    {
        var state = ClosedSnapshot();
        var schedule = new RecoveryAttemptSchedule();
        Assert.True(schedule.ShouldStart(state, true, 0));
        schedule.Starting(state, 0);
        Assert.False(schedule.ShouldStart(state, false, 5000));
        Assert.True(schedule.ShouldStart(state, true, 5000)); // known failure may retry
        var success = state with { FinalRemovalIntentSequence = 2, FinalRemovalReturnSequence = 3 };
        Assert.False(schedule.ShouldStart(success, true, 5000));
        var lateReturn = success with { LastCreationReturnSequence = 4 };
        Assert.True(schedule.ShouldStart(lateReturn, true, 5000));
        schedule.Starting(lateReturn, 5000);
        var removedAgain = lateReturn with { FinalRemovalIntentSequence = 5, FinalRemovalReturnSequence = 6 };
        Assert.False(schedule.ShouldStart(removedAgain, true, 10000));
        Assert.True(schedule.ShouldStart(removedAgain with { CreatorExited = true }, true, 10000));
    }

    [Fact]
    public void Unknown_remove_intent_does_not_spawn_more_workers_after_its_process_dies()
    {
        var recovery = new WorkerIdentity(2345, 56789, Guid.NewGuid(), WorkerRole.Recovery);
        var unknown = ClosedSnapshot() with
        {
            IncompleteReason = "UnknownInFlight",
            Pending = [new(Guid.NewGuid(), recovery, NativeOperation.RemoveDevice, 2)]
        };
        var schedule = new RecoveryAttemptSchedule();
        Assert.False(schedule.ShouldStart(unknown, true, 1000000));
        Assert.False(schedule.ShouldStart(ClosedSnapshot() with { IncompleteReason = "OwnershipConflict" }, true, 1000000));
        Assert.False(schedule.ShouldStart(ClosedSnapshot() with { ProbeStopped = false }, true, 1000000));
        Assert.False(schedule.ShouldStart(ClosedSnapshot() with { Retired = true }, true, 1000000));
    }

    private static RunSnapshot ClosedSnapshot() => new(RunId, 1, true, false, true, false, null, false,
        CreatorPermit.Revoked, null, false, null, true, [], [], 0, 0, 0, 0, null);

    [Fact]
    public void Same_revision_does_not_renew_health_and_stale_revisions_are_rejected()
    {
        var runtime = Runtime();
        var progress = new RunnerProgress(runtime, 1000);
        var control = Control(runtime);
        Assert.True(progress.Observe(control, 1200));
        Assert.False(progress.Observe(control, 9000));
        Assert.Equal(1200, progress.LastHeartbeatMs);
        Assert.Throws<InvalidDataException>(() => progress.Observe(control with { Close = true }, 9001));
        Assert.True(progress.Observe(control with { Revision = 2 }, 10000));
        Assert.Throws<InvalidDataException>(() => progress.Observe(control, 10001));
    }

    [Fact]
    public void Control_cannot_change_run_probe_identity_or_retract_close()
    {
        var runtime = Runtime();
        var progress = new RunnerProgress(runtime, 1);
        var probe = new WorkerIdentity(2345, 456789, runtime.ProbeNonce, WorkerRole.Probe);
        var control = Control(runtime) with { Probe = probe, ProofReady = true, Close = true };
        Assert.True(progress.Observe(control, 2));
        Assert.Throws<InvalidDataException>(() => progress.Observe(control with { Revision = 2, RunId = Guid.NewGuid() }, 3));
        Assert.Throws<InvalidDataException>(() => progress.Observe(control with { Revision = 2, Probe = probe with { Pid = 3456 } }, 3));
        Assert.Throws<InvalidDataException>(() => progress.Observe(control with { Revision = 2, Close = false }, 3));
        Assert.Throws<InvalidDataException>(() => progress.Observe(control with { Revision = 2, ProofReady = false }, 3));
    }

    [Fact]
    public void Mailbox_never_creates_missing_preparation_and_rejects_torn_or_oversized_records()
    {
        Directory.CreateDirectory(directory);
        Assert.Throws<FileNotFoundException>(() => RunnerControlMailbox.Write(directory, Control(Runtime())));
        var path = Path.Combine(directory, RunnerControlMailbox.FileName);
        File.WriteAllBytes(path, []);
        var control = Control(Runtime());
        RunnerControlMailbox.Write(directory, control);
        Assert.Equal(control, RunnerControlMailbox.Read(directory));
        File.WriteAllText(path, "{");
        Assert.Throws<System.Text.Json.JsonException>(() => RunnerControlMailbox.Read(directory));
        File.WriteAllBytes(path, new byte[RunnerControlMailbox.MaximumBytes + 1]);
        Assert.Throws<InvalidDataException>(() => RunnerControlMailbox.Read(directory));
    }

    [Fact]
    public async Task Parked_probe_permission_matches_actual_process_and_denies_pid_reuse()
    {
        Directory.CreateDirectory(directory);
        using var current = Process.GetCurrentProcess();
        var nonce = Guid.NewGuid();
        var permission = new ProbeStartAuthorization(1, RunId, nonce, current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc());
        var path = Path.Combine(directory, ProbeStartAuthorization.FileName);
        File.WriteAllBytes(path, ContractJson.Write(permission));
        await ProbeStartAuthorization.WaitForCurrentProcessAsync(path, RunId, nonce, CancellationToken.None);
        File.WriteAllBytes(path, ContractJson.Write(permission with { CreationFileTime = permission.CreationFileTime + 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProbeStartAuthorization.WaitForCurrentProcessAsync(path, RunId, nonce, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProbeStartAuthorization.WaitForCurrentProcessAsync(path, RunId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Parked_probe_cancellation_does_not_need_driver_or_permission_file()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProbeStartAuthorization.WaitForCurrentProcessAsync(
            Path.Combine(directory, "missing.json"), RunId, Guid.NewGuid(), cancellation.Token));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
