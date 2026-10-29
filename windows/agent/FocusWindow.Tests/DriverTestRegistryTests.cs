using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DriverTestRegistryTests : IDisposable
{
    private sealed class Clock : IDriverTestClock { public long Milliseconds => 1000; }
    private sealed class FencedVerifier(RunRegistry registry) : IPackageAdmissionVerifier
    {
        public ManualResetEventSlim? TrustStarted { get; set; }
        public ManualResetEventSlim? ContinueTrust { get; set; }
        public IPackageAdmissionLease AcquireForCreation(RunManifest manifest, NativeOperation? operation)
        {
            // Simulate expensive trust and protected file custody outside the short gate.
            _ = registry.WithBoundApproval(manifest, approval => approval.ApprovalId);
            TrustStarted?.Set();
            ContinueTrust?.Wait();
            return new Lease(registry, manifest);
        }
        private sealed class Lease(RunRegistry registry, RunManifest manifest) : IPackageAdmissionLease
        {
            public T CommitAdmission<T>(Func<T> commit) => registry.WithBoundApproval(manifest, _ => commit());
            public void Dispose() { }
        }
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "deskfuse-registry-test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] policy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-test-policy.json"));
    private RunManifest Manifest()
    {
        var id = Guid.NewGuid();
        return new(1, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            ContractJson.Hash(policy), new() { ["helper.exe"] = new string('a', 64) },
            RunManifest.WatchName(id), RunManifest.BootName(id));
    }
    private (RunManifest Manifest, PackageApproval Approval) ApprovedManifest()
    {
        var id = Guid.NewGuid();
        var files = new PackageFile[] { new("FocusIdd.cat", new string('a', 64)),
            new("FocusIdd.dll", new string('b', 64)), new("FocusIdd.inf", new string('c', 64)) };
        var approval = new PackageApproval(1, Guid.NewGuid(), "FocusIdd.Umdf", "x64", PackageProfile.LocalTest,
            new PackageIdentity(files).Digest(), files, "FocusIdd.cat", new string('a', 64),
            [new string('d', 64)], 1, "S-1-5-21-1", PackageApprovalState.Available, null);
        Directory.CreateDirectory(Path.Combine(root, "approvals"));
        var bytes = ContractJson.Write(approval);
        File.WriteAllBytes(Path.Combine(root, "approvals", approval.ApprovalId.ToString("N") + ".json"), bytes);
        var binding = new PackageAdmissionBinding(approval.Profile, approval.ApprovalId, ContractJson.Hash(bytes),
            approval.PackageContentDigest, new string('e', 64));
        return (new(2, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            ContractJson.Hash(policy), new() { ["helper.exe"] = new string('a', 64) },
            RunManifest.WatchName(id), RunManifest.BootName(id), binding), approval);
    }
    private void Retire(RunJournal journal)
    {
        var core = new RunCoordinator(journal, policy, new Clock());
        core.Close("CancelledBeforeProvisioning");
        var recovery = new WorkerIdentity(1000, 123456789, Guid.NewGuid(), WorkerRole.Recovery);
        core.Execute(recovery, NativeOperation.RemoveDevice, _ => new(true,
            ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
        Assert.True(core.TryRetire(m => new(m.RunId, m.InstanceId, true, true)));
    }

    [Fact]
    public void Releasing_the_process_lock_does_not_erase_the_durable_active_run()
    {
        var registry = new RunRegistry(root);
        var first = registry.Reserve(Manifest());
        Assert.Throws<InvalidOperationException>(() => new RunRegistry(root).Reserve(Manifest()));
        Assert.Throws<InvalidOperationException>(() => registry.ReleaseRetired(first.Manifest.RunId));
    }

    [Fact]
    public void Lost_active_pointer_still_cannot_hide_an_unresolved_run()
    {
        var registry = new RunRegistry(root);
        _ = registry.Reserve(Manifest());
        File.Delete(Path.Combine(root, "active.json"));
        Assert.Throws<InvalidOperationException>(() => registry.Reserve(Manifest()));
    }

    [Fact]
    public void Retired_old_cleanup_cannot_clear_a_new_runs_active_record()
    {
        var registry = new RunRegistry(root);
        var first = registry.Reserve(Manifest());
        Retire(first);
        // Simulates a crash after Retired, before deleting tasks/active pointer.
        var second = registry.Reserve(Manifest());
        Assert.False(registry.ReleaseRetired(first.Manifest.RunId));
        Assert.Equal(second.Manifest.RunId, ContractJson.Read<Guid>(File.ReadAllBytes(Path.Combine(root, "active.json"))));
        Assert.True(registry.Open(first.Manifest.RunId).Snapshot().Retired);
        Assert.NotEqual(first.Manifest.InstanceId, second.Manifest.InstanceId);
    }

    [Fact]
    public void A_torn_old_record_blocks_new_admission_and_is_not_automatically_deleted()
    {
        var registry = new RunRegistry(root);
        var first = registry.Reserve(Manifest());
        var path = Path.Combine(root, "runs", first.Manifest.RunId.ToString("N"), "journal.jsonl");
        File.AppendAllText(path, "torn");
        Assert.Throws<InvalidDataException>(() => registry.Reserve(Manifest()));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Approved_candidate_is_bound_once_and_cannot_be_reused_by_another_run()
    {
        var (manifest, approval) = ApprovedManifest();
        var registry = new RunRegistry(root);
        var first = registry.Reserve(manifest);
        Assert.Equal(approval.ApprovalId, registry.WithBoundApproval(manifest, held => held.ApprovalId));
        var forged = manifest with { PackageAdmission = manifest.PackageAdmission! with { VerificationRecordSha256 = new string('f', 64) } };
        Assert.Throws<InvalidDataException>(() => registry.WithBoundApproval(forged, _ => 0));
        Retire(first);
        var secondId = Guid.NewGuid();
        var second = manifest with { RunId = secondId, InstanceId = RunManifest.DeviceId(secondId),
            WatchTask = RunManifest.WatchName(secondId), BootTask = RunManifest.BootName(secondId) };
        Assert.Throws<IOException>(() => registry.Reserve(second));
        Assert.True(File.Exists(Path.Combine(root, "approvals", approval.ApprovalId.ToString("N") + ".bound.json")));
    }

    [Fact]
    public void Changed_approval_or_revocation_denies_new_admission_but_not_exact_run_recovery()
    {
        var (manifest, approval) = ApprovedManifest();
        var registry = new RunRegistry(root);
        var journal = registry.Reserve(manifest);
        var path = Path.Combine(root, "approvals", approval.ApprovalId.ToString("N") + ".json");
        var original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, ContractJson.Write(approval with { ApprovedByWindowsSid = "S-1-5-21-2" }));
        Assert.Throws<InvalidDataException>(() => registry.WithBoundApproval(manifest, _ => 0));
        File.WriteAllBytes(path, original);
        registry.RevokeApproval(approval.ApprovalId);
        Assert.Throws<InvalidOperationException>(() => registry.WithBoundApproval(manifest, _ => 0));
        File.Delete(path);
        Retire(journal); // Recovery does not require a surviving or trusted package.
        Assert.True(registry.ReleaseRetired(manifest.RunId));
    }

    [Fact]
    public void Preexisting_or_torn_binding_marker_burns_approval_and_never_adopts_it()
    {
        var (manifest, approval) = ApprovedManifest();
        var marker = Path.Combine(root, "approvals", approval.ApprovalId.ToString("N") + ".bound.json");
        File.WriteAllText(marker, "torn");
        Assert.Throws<IOException>(() => new RunRegistry(root).Reserve(manifest));
        Assert.Equal("torn", File.ReadAllText(marker));
    }

    [Fact]
    public async Task Revocation_is_serialized_against_the_short_durable_admission_commit()
    {
        var (manifest, approval) = ApprovedManifest();
        var registry = new RunRegistry(root);
        _ = registry.Reserve(manifest);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblockCommit = new ManualResetEventSlim();
        var committing = Task.Run(() => registry.WithBoundApproval(manifest, _ =>
        {
            started.SetResult();
            unblockCommit.Wait();
            return 0;
        }));
        await started.Task;
        var revoke = Task.Run(() => registry.RevokeApproval(approval.ApprovalId));
        await Task.Delay(100);
        Assert.False(revoke.IsCompleted);
        unblockCommit.Set();
        await committing;
        await revoke;
        Assert.Throws<InvalidOperationException>(() => registry.WithBoundApproval(manifest, _ => 0));
    }

    [Fact]
    public async Task Revocation_during_trust_verification_prevents_late_native_dispatch_and_survives_restart()
    {
        var (manifest, approval) = ApprovedManifest();
        var registry = new RunRegistry(root);
        var journal = registry.Reserve(manifest);
        var verifier = new FencedVerifier(registry);
        var core = new RunCoordinator(journal, policy, new Clock(), verifier);
        var watch = new WorkerIdentity(1001, 10, Guid.NewGuid(), WorkerRole.Watch);
        var creator = new WorkerIdentity(1002, 11, Guid.NewGuid(), WorkerRole.Creator);
        var recovery = new WorkerIdentity(1003, 12, Guid.NewGuid(), WorkerRole.Recovery);
        core.Arm(new(manifest.RunId, watch, manifest.BootTask, true, true));
        core.RegisterCreator(creator);
        using var started = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        verifier.TrustStarted = started;
        verifier.ContinueTrust = proceed;
        var calls = 0;
        var attempt = Task.Run(() => core.Execute(creator, NativeOperation.StagePackage, _ => { calls++; return new(true); }));
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            registry.RevokeApproval(approval.ApprovalId);
        }
        finally { proceed.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await attempt);
        Assert.Equal(0, calls);
        Assert.True(core.Snapshot().Closing);
        Assert.Throws<InvalidOperationException>(() => new RunRegistry(root).WithBoundApproval(manifest, _ => 0));
        core.ObserveExitedWorker(creator);
        core.Execute(recovery, NativeOperation.RemoveDevice, _ => new(true,
            ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
        Assert.True(core.TryRetire(m => new(m.RunId, m.InstanceId, true, true)));
    }

    [Fact]
    public async Task Hung_native_creation_does_not_block_revocation_or_authorize_a_later_creation()
    {
        var (manifest, approval) = ApprovedManifest();
        var registry = new RunRegistry(root);
        var journal = registry.Reserve(manifest);
        var core = new RunCoordinator(journal, policy, new Clock(), new FencedVerifier(registry));
        var watch = new WorkerIdentity(1001, 10, Guid.NewGuid(), WorkerRole.Watch);
        var creator = new WorkerIdentity(1002, 11, Guid.NewGuid(), WorkerRole.Creator);
        var recovery = new WorkerIdentity(1003, 12, Guid.NewGuid(), WorkerRole.Recovery);
        core.Arm(new(manifest.RunId, watch, manifest.BootTask, true, true));
        core.RegisterCreator(creator);
        using var nativeStarted = new ManualResetEventSlim();
        using var nativeReturn = new ManualResetEventSlim();
        var first = Task.Run(() => core.Execute(creator, NativeOperation.StagePackage, _ =>
        {
            nativeStarted.Set();
            nativeReturn.Wait();
            return new(true);
        }));
        try
        {
            Assert.True(nativeStarted.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() => registry.RevokeApproval(approval.ApprovalId)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(first.IsCompleted);
        }
        finally { nativeReturn.Set(); }
        await first;
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => core.Execute(creator, NativeOperation.CreateElement,
            _ => { calls++; return new(true); }));
        Assert.Equal(0, calls);
        Assert.True(core.Snapshot().Closing);
        core.ObserveExitedWorker(creator);
        core.Execute(recovery, NativeOperation.RemoveDevice, _ => new(true,
            ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
        Assert.True(core.TryRetire(m => new(m.RunId, m.InstanceId, true, true)));
    }

    [Fact]
    public void Revocation_remains_effective_after_approval_disappears_or_tombstone_is_torn()
    {
        var (manifest, approval) = ApprovedManifest();
        var registry = new RunRegistry(root);
        _ = registry.Reserve(manifest);
        var path = Path.Combine(root, "approvals", approval.ApprovalId.ToString("N") + ".json");
        var original = File.ReadAllBytes(path);
        File.Delete(path);
        registry.RevokeApproval(approval.ApprovalId);
        File.WriteAllBytes(path, original);
        var marker = Path.Combine(root, "approvals", approval.ApprovalId.ToString("N") + ".revoked.json");
        File.WriteAllText(marker, "torn");
        Assert.Throws<InvalidOperationException>(() => new RunRegistry(root).WithBoundApproval(manifest, _ => 0));
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
