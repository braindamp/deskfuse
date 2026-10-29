using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DriverTestRecoveryTests : IDisposable
{
    private sealed class Clock : IDriverTestClock { public long Milliseconds { get; set; } = 1000; }
    private sealed class FakePackageVerifier : IPackageAdmissionVerifier
    {
        public bool Reject { get; set; }
        public int ActiveLeases { get; private set; }
        private sealed class Lease(FakePackageVerifier owner) : IPackageAdmissionLease
        {
            private FakePackageVerifier? current = owner;
            public T CommitAdmission<T>(Func<T> commit) => commit();
            public void Dispose() { if (Interlocked.Exchange(ref current, null) is { } value) value.ActiveLeases--; }
        }
        public IPackageAdmissionLease AcquireForCreation(RunManifest manifest, NativeOperation? operation)
        {
            if (Reject) throw new InvalidOperationException("Simulated trust or approval failure.");
            ActiveLeases++;
            return new Lease(this);
        }
    }
    private readonly FakePackageVerifier verifier = new();
    private readonly string parent = Path.Combine(Path.GetTempPath(), "deskfuse-recovery-test-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly RunJournal journal;
    private readonly RunCoordinator coordinator;
    private readonly WorkerIdentity creator = Worker(WorkerRole.Creator);
    private readonly WorkerIdentity recovery = Worker(WorkerRole.Recovery);
    private readonly WorkerIdentity watch = Worker(WorkerRole.Watch);
    private readonly SafetyPolicy policy;
    private readonly string directory;
    private static readonly NativeOperation[] Steps = [NativeOperation.StagePackage, NativeOperation.CreateElement,
        NativeOperation.SetHardwareId, NativeOperation.RegisterDevice, NativeOperation.InstallDevice];

    public DriverTestRecoveryTests()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-test-policy.json"));
        policy = ContractJson.Read<SafetyPolicy>(bytes);
        var id = Guid.NewGuid();
        directory = Path.Combine(parent, id.ToString("N"));
        var manifest = new RunManifest(2, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            ContractJson.Hash(bytes), new() { ["DeskFuse.DriverTestOwner.exe"] = new string('a', 64) },
            RunManifest.WatchName(id), RunManifest.BootName(id), new(PackageProfile.LocalTest, Guid.NewGuid(),
                new string('a', 64), new string('b', 64), new string('c', 64)));
        journal = RunJournal.Create(directory, manifest);
        coordinator = new(journal, bytes, clock, verifier);
    }

    private static WorkerIdentity Worker(WorkerRole role) => new(1000 + (int)role, 123456789, Guid.NewGuid(), role);
    private void Arm() => coordinator.Arm(new(journal.Manifest.RunId, watch, journal.Manifest.BootTask, true, true));
    private void Begin() { Arm(); coordinator.RegisterCreator(creator); }
    private void Provision()
    {
        Begin();
        foreach (var step in Steps) coordinator.Execute(creator, step, _ => new(true));
        coordinator.RelinquishCreator(creator);
        coordinator.ObserveExitedWorker(creator);
    }
    private void Ready()
    {
        Provision();
        coordinator.RegisterProbe(Worker(WorkerRole.Probe));
        coordinator.MarkProofReady();
    }
    private void Cleanup() => coordinator.Execute(recovery, NativeOperation.RemoveDevice, _ => new(true,
        ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
    private AbsenceObservation Absent(RunManifest manifest) => new(manifest.RunId, manifest.InstanceId, true, true);
    private void At(long elapsed) => clock.Milliseconds = 1000 + elapsed;

    [Fact]
    public void Missing_verifier_cannot_admit_creation_but_cleanup_remains_available()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-test-policy.json"));
        var noVerifier = new RunCoordinator(journal, bytes, clock);
        noVerifier.Arm(new(journal.Manifest.RunId, watch, journal.Manifest.BootTask, true, true));
        Assert.Throws<InvalidOperationException>(() => noVerifier.RegisterCreator(creator));
        Assert.True(noVerifier.Snapshot().Closing);
        noVerifier.Execute(recovery, NativeOperation.RemoveDevice, _ => new(true,
            ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
        Assert.True(noVerifier.TryRetire(Absent));
    }

    [Fact]
    public void Lost_admission_before_install_closes_creation_but_does_not_strand_recovery()
    {
        Begin();
        foreach (var step in Steps.Take(4)) coordinator.Execute(creator, step, _ => new(true));
        verifier.Reject = true;
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => coordinator.Execute(creator, NativeOperation.InstallDevice,
            _ => { calls++; return new(true); }));
        Assert.Equal(0, calls);
        Assert.True(coordinator.Snapshot().Closing);
        coordinator.ObserveExitedWorker(creator);
        Cleanup();
        Assert.True(coordinator.TryRetire(Absent));
    }

    [Fact]
    public void Package_custody_is_held_through_native_return_and_released_on_exception()
    {
        Begin();
        Assert.Equal(0, verifier.ActiveLeases);
        Assert.Throws<IOException>(() => coordinator.Execute(creator, NativeOperation.StagePackage, _ =>
        {
            Assert.Equal(1, verifier.ActiveLeases);
            throw new IOException("native call interrupted");
        }));
        Assert.Equal(0, verifier.ActiveLeases);
        Assert.Single(coordinator.Snapshot().Pending);
        coordinator.ObserveExitedWorker(creator);
        Assert.Equal("UnknownInFlight", coordinator.Snapshot().IncompleteReason);
    }

    [Fact]
    public void Naked_package_intent_is_rejected_without_custody()
    {
        Begin();
        Assert.Throws<InvalidOperationException>(() => coordinator.Admit(creator, NativeOperation.StagePackage));
        Assert.Empty(coordinator.Snapshot().Pending);
        Assert.Equal(0, verifier.ActiveLeases);
    }

    [Fact]
    public void Unverified_recovery_and_unarmed_creation_admit_no_native_work()
    {
        Assert.Throws<InvalidOperationException>(() => coordinator.RegisterCreator(creator));
        Assert.Throws<InvalidOperationException>(() => coordinator.Arm(new(journal.Manifest.RunId,
            watch, journal.Manifest.BootTask, false, true)));
        Assert.Empty(coordinator.Snapshot().Pending);
        Assert.False(coordinator.Snapshot().Armed);
    }

    [Fact]
    public void Closing_before_delayed_launch_permanently_revokes_creation()
    {
        Arm();
        coordinator.Close("Cancelled");
        Assert.Throws<InvalidOperationException>(() => coordinator.RegisterCreator(creator));
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => coordinator.Execute(creator, NativeOperation.StagePackage,
            _ => { calls++; return new(true); }));
        Assert.Equal(0, calls);
        Cleanup();
        Assert.True(coordinator.TryRetire(Absent));
    }

    [Fact]
    public void Intent_is_durable_before_native_dispatch_and_return_is_not_invented()
    {
        Begin();
        Assert.Throws<IOException>(() => coordinator.Execute(creator, NativeOperation.StagePackage, manifest =>
        {
            var durable = new RunJournal(directory).Snapshot();
            Assert.Equal(creator, Assert.Single(durable.Pending).Worker);
            Assert.Equal(journal.Manifest.InstanceId, manifest.InstanceId);
            throw new IOException("simulated abrupt native boundary failure");
        }));
        coordinator.ObserveExitedWorker(creator);
        Cleanup();
        Assert.Equal("UnknownInFlight", coordinator.Snapshot().IncompleteReason);
        Assert.False(coordinator.TryRetire(Absent));
        Assert.Throws<InvalidOperationException>(() => coordinator.RegisterCreator(Worker(WorkerRole.Creator)));
    }

    [Fact]
    public void Early_emergency_absence_cannot_retire_a_late_install()
    {
        Begin();
        foreach (var step in Steps[..^1]) coordinator.Execute(creator, step, _ => new(true));
        using var installing = coordinator.BeginPackageOperation(creator, NativeOperation.InstallDevice);
        coordinator.Close("Deadline");
        Cleanup();
        Assert.False(coordinator.TryRetire(Absent));
        installing.Returned(new(true));
        coordinator.RelinquishCreator(creator);
        coordinator.ObserveExitedWorker(creator);
        // The earlier removal was admitted before the last creation return.
        Assert.False(coordinator.TryRetire(Absent));
        Cleanup();
        Assert.True(coordinator.TryRetire(Absent));
    }

    [Fact]
    public void Only_one_creation_and_one_emergency_removal_may_be_outstanding()
    {
        Begin();
        using var staging = coordinator.BeginPackageOperation(creator, NativeOperation.StagePackage);
        Assert.Throws<InvalidOperationException>(() => coordinator.Admit(creator, NativeOperation.CreateElement));
        coordinator.Close("Abort");
        _ = coordinator.Admit(recovery, NativeOperation.RemoveDevice);
        Assert.Throws<InvalidOperationException>(() => coordinator.Admit(Worker(WorkerRole.Recovery), NativeOperation.RemoveDevice));
        Assert.Equal(2, coordinator.Snapshot().Pending.Length);
    }

    [Fact]
    public void Wrong_incarnation_cannot_acknowledge_or_mark_another_worker_dead()
    {
        Begin();
        using var staging = coordinator.BeginPackageOperation(creator, NativeOperation.StagePackage);
        var id = staging.Id;
        var reusedPid = creator with { CreationFileTime = creator.CreationFileTime + 1 };
        Assert.Throws<InvalidOperationException>(() => coordinator.Returned(reusedPid, id, new(true)));
        Assert.Throws<InvalidOperationException>(() => coordinator.ObserveExitedWorker(reusedPid));
        Assert.Single(coordinator.Snapshot().Pending);
        Assert.False(coordinator.Snapshot().CreatorExited);
    }

    [Theory]
    [InlineData(1, 59999)]
    [InlineData(1, 85000)]
    [InlineData(2, 205000)]
    [InlineData(3, 325000)]
    public void Early_or_late_ack_closes_without_extending_the_policy(int index, long elapsed)
    {
        Ready();
        for (var i = 1; i < index; i++) { At(policy.PromptAtMs[i - 1]); Assert.True(coordinator.Acknowledge(journal.Manifest.RunId, i)); }
        At(elapsed);
        Assert.False(coordinator.Acknowledge(journal.Manifest.RunId, index));
        Assert.True(coordinator.Snapshot().Closing);
        At(300000);
        Assert.False(coordinator.Acknowledge(journal.Manifest.RunId, 3));
    }

    [Fact]
    public void Acknowledgements_are_ordered_run_bound_and_do_not_disable_later_deadlines()
    {
        Ready();
        At(60000);
        Assert.False(coordinator.Acknowledge(Guid.NewGuid(), 1));
        Assert.False(coordinator.Snapshot().Closing);
        Assert.True(coordinator.Acknowledge(journal.Manifest.RunId, 1));
        At(205000); // Suspend/resume crosses the next deadline; heartbeats alone do not renew it.
        coordinator.CheckHealth(clock.Milliseconds, clock.Milliseconds);
        Assert.True(coordinator.Snapshot().Closing);
    }

    [Fact]
    public void Third_ack_closes_but_cleanup_waits_for_the_exact_probe_to_stop()
    {
        Ready();
        for (var i = 1; i <= 3; i++) { At(policy.PromptAtMs[i - 1]); Assert.True(coordinator.Acknowledge(journal.Manifest.RunId, i)); }
        Assert.True(coordinator.Snapshot().Closing);
        Cleanup();
        Assert.False(coordinator.TryRetire(Absent));
        Assert.Throws<InvalidOperationException>(() => coordinator.ObserveProbeStopped(Worker(WorkerRole.Probe)));
        coordinator.ObserveProbeStopped(coordinator.Snapshot().Probe!);
        Assert.True(coordinator.TryRetire(Absent));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Either_stale_control_heartbeat_closes_even_after_an_ack(bool runnerStale)
    {
        Ready(); At(60000); Assert.True(coordinator.Acknowledge(journal.Manifest.RunId, 1));
        At(70000);
        coordinator.CheckHealth(runnerStale ? 61000 : clock.Milliseconds,
            runnerStale ? clock.Milliseconds : 61000);
        Assert.True(coordinator.Snapshot().Closing);
    }

    [Fact]
    public void Incomplete_provisioning_cannot_reset_the_first_prompt_clock()
    {
        Begin(); At(60000);
        coordinator.CheckHealth(clock.Milliseconds, clock.Milliseconds);
        Assert.True(coordinator.Snapshot().Closing);
        Assert.Throws<InvalidOperationException>(() => coordinator.Arm(new(journal.Manifest.RunId,
            watch, journal.Manifest.BootTask, true, true)));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Failed_removal_or_uncertain_absence_never_retires(bool success, bool absent, bool enumerated)
    {
        Provision(); coordinator.Close("Stop");
        coordinator.Execute(recovery, NativeOperation.RemoveDevice, _ => new(success,
            ParentAndSubtreeAbsent: absent, EnumerationSucceeded: enumerated));
        Assert.False(coordinator.TryRetire(Absent));
    }

    [Fact]
    public void Reboot_requirement_is_latched_even_after_a_later_clean_observation()
    {
        Begin();
        coordinator.Execute(creator, NativeOperation.StagePackage, _ => new(true, RebootRequired: true));
        coordinator.RelinquishCreator(creator);
        coordinator.ObserveExitedWorker(creator);
        Cleanup();
        Assert.True(coordinator.Snapshot().RebootRequired);
        Assert.False(coordinator.TryRetire(Absent));
    }

    [Fact]
    public void Fresh_absence_is_required_and_a_removal_admitted_during_observation_fences_retirement()
    {
        Provision(); coordinator.Close("Stop"); Cleanup();
        Assert.False(coordinator.TryRetire(m => Absent(m) with { EnumerationSucceeded = false }));
        Guid retry = default;
        Assert.False(coordinator.TryRetire(m => { retry = coordinator.Admit(recovery, NativeOperation.RemoveDevice); return Absent(m); }));
        coordinator.Returned(recovery, retry, new(true, ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
        Assert.True(coordinator.TryRetire(Absent));
        Assert.Throws<InvalidOperationException>(() => coordinator.Admit(recovery, NativeOperation.RemoveDevice));
        Assert.Throws<InvalidOperationException>(() => coordinator.RegisterCreator(Worker(WorkerRole.Creator)));
        coordinator.Close("LateOldTask"); // Idempotent no-op after durable retirement.
        Assert.True(new RunJournal(directory).Snapshot().Retired);
    }

    [Fact]
    public void Fresh_absence_for_a_different_run_or_instance_is_rejected()
    {
        Provision(); coordinator.Close("Stop"); Cleanup();
        Assert.False(coordinator.TryRetire(m => Absent(m) with { RunId = Guid.NewGuid() }));
        Assert.False(coordinator.TryRetire(m => Absent(m) with { InstanceId = @"ROOT\DISPLAY\0000" }));
    }

    [Fact]
    public void A_dead_removal_worker_does_not_block_scoped_boot_retry_or_erase_unknown_history()
    {
        Provision(); coordinator.Close("Stop");
        var lost = coordinator.Admit(recovery, NativeOperation.RemoveDevice);
        coordinator.ObserveExitedWorker(recovery);
        var restarted = Worker(WorkerRole.Recovery);
        coordinator.Execute(restarted, NativeOperation.RemoveDevice, _ => new(true,
            ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
        Assert.Equal("UnknownInFlight", coordinator.Snapshot().IncompleteReason);
        Assert.False(coordinator.TryRetire(Absent));
        Assert.Throws<InvalidOperationException>(() => coordinator.Returned(recovery, lost, new(true)));
        Assert.Throws<InvalidOperationException>(() => coordinator.Admit(recovery, NativeOperation.RemoveDevice));
    }

    [Fact]
    public void Verified_creator_exit_between_calls_fences_creation_without_inventing_unknown_native_work()
    {
        Begin();
        coordinator.Execute(creator, NativeOperation.StagePackage, _ => new(true));
        coordinator.ObserveExitedWorker(creator);
        Assert.True(coordinator.Snapshot().Closing);
        Assert.Null(coordinator.Snapshot().IncompleteReason);
        Assert.Throws<InvalidOperationException>(() => coordinator.Admit(creator, NativeOperation.CreateElement));
        Cleanup();
        Assert.True(coordinator.TryRetire(Absent));
    }

    [Fact]
    public void Policy_bytes_are_bound_to_the_manifest_even_when_only_whitespace_changes()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-test-policy.json"));
        Assert.Throws<InvalidDataException>(() => new RunCoordinator(journal, [.. bytes, (byte)' '], clock));
    }

    [Fact]
    public void An_actual_journal_append_failure_prevents_native_dispatch()
    {
        Begin();
        var path = Path.Combine(directory, "journal.jsonl");
        var calls = 0;
        using (var denyWrite = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => coordinator.Execute(creator, NativeOperation.StagePackage,
                _ => { calls++; return new(true); }));
        Assert.Equal(0, calls);
        Assert.Empty(coordinator.Snapshot().Pending);
    }

    [Fact]
    public void A_collision_never_authorizes_removal_of_the_existing_instance()
    {
        Begin();
        coordinator.Execute(creator, NativeOperation.StagePackage, _ => new(true));
        coordinator.Execute(creator, NativeOperation.CreateElement,
            _ => new(false, unchecked((int)0xE0000207), OwnershipConflict: true));
        Assert.Equal("OwnershipConflict", coordinator.Snapshot().IncompleteReason);
        Assert.Throws<InvalidOperationException>(() => Cleanup());
        Assert.False(coordinator.TryRetire(Absent));
    }

    [Fact]
    public void Native_admission_itself_checks_the_deadline_even_before_watchdog_polling()
    {
        Begin(); At(60000);
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => coordinator.Execute(creator, NativeOperation.StagePackage,
            _ => { calls++; return new(true); }));
        Assert.Equal(0, calls);
        Assert.True(coordinator.Snapshot().Closing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Torn_or_modified_journal_is_never_treated_as_a_clean_snapshot(bool torn)
    {
        Begin();
        var path = Path.Combine(directory, "journal.jsonl");
        var bytes = File.ReadAllBytes(path);
        if (torn) Array.Resize(ref bytes, bytes.Length - 1);
        else bytes[bytes.Length / 2] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.ThrowsAny<Exception>(() => new RunJournal(directory).Snapshot());
    }

    [Fact]
    public void Run_guid_and_manifest_cannot_be_adopted_reused_or_retargeted()
    {
        Assert.Throws<IOException>(() => RunJournal.Create(directory, journal.Manifest));
        var bytes = File.ReadAllBytes(Path.Combine(directory, "run.json"));
        var altered = journal.Manifest with { InstanceId = @"ROOT\DISPLAY\0000" };
        File.WriteAllBytes(Path.Combine(directory, "run.json"), ContractJson.Write(altered));
        Assert.Throws<InvalidDataException>(() => new RunJournal(directory));
        File.WriteAllBytes(Path.Combine(directory, "run.json"), bytes);
        var changed = journal.Manifest with { BundleSha256 = new() { ["DeskFuse.DriverTestOwner.exe"] = new string('b', 64) } };
        File.WriteAllBytes(Path.Combine(directory, "run.json"), ContractJson.Write(changed));
        Assert.Throws<InvalidOperationException>(() => new RunJournal(directory).Snapshot());
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(parent)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }
}
