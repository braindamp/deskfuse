using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class RunExecutionTests : IDisposable
{
    private sealed class Clock : IDriverTestClock { public long Milliseconds { get; set; } = 1000; }
    private sealed class Verifier : IPackageAdmissionVerifier
    {
        private sealed class Lease : IPackageAdmissionLease
        {
            public T CommitAdmission<T>(Func<T> commit) => commit();
            public void Dispose() { }
        }
        public IPackageAdmissionLease AcquireForCreation(RunManifest manifest, NativeOperation? operation) => new Lease();
    }
    private sealed class Native(RunManifest manifest) : IDriverNativeOperations
    {
        public List<NativeOperation> Calls { get; } = [];
        public Func<NativeOperation, NativeResult> Result { get; set; } = _ => new(true);
        public NativeResult Invoke(NativeOperation operation) { Calls.Add(operation); return Result(operation); }
        public AbsenceObservation ObserveAbsence() => new(manifest.RunId, manifest.InstanceId, true, true);
    }
    private readonly string parent = Path.Combine(Path.GetTempPath(), "deskfuse-execution-" + Guid.NewGuid().ToString("N"));
    private readonly RunCoordinator owner;
    private readonly Clock clock = new();
    private readonly Native native;
    private readonly WorkerIdentity creator = Worker(WorkerRole.Creator), recovery = Worker(WorkerRole.Recovery);
    private static WorkerIdentity Worker(WorkerRole role) => new(1000 + (int)role, 123456789, Guid.NewGuid(), role);

    public RunExecutionTests()
    {
        var policy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-test-policy.json"));
        var id = Guid.NewGuid();
        var hash = new string('a', 64);
        var manifest = new RunManifest(2, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            ContractJson.Hash(policy), new() { ["helper.exe"] = hash }, RunManifest.WatchName(id), RunManifest.BootName(id),
            new(PackageProfile.LocalTest, Guid.NewGuid(), hash, hash, hash));
        owner = new(RunJournal.Create(Path.Combine(parent, id.ToString("N")), manifest), policy, clock, new Verifier());
        owner.Arm(new(id, Worker(WorkerRole.Watch), manifest.BootTask, true, true));
        native = new(manifest);
    }

    [Fact]
    public void Successful_provisioning_relinquishes_but_does_not_claim_process_exit()
    {
        var state = new RunProvisioner(owner, native).Run(creator, CancellationToken.None);
        Assert.Equal(5, native.Calls.Count);
        Assert.Equal(CreatorPermit.Relinquished, state.Permit);
        Assert.False(state.CreatorExited);
        Assert.False(state.ProofReady);
        Assert.Throws<InvalidOperationException>(() => new RunProvisioner(owner, native).Run(creator, CancellationToken.None));
        Assert.Equal(5, native.Calls.Count);
    }

    [Fact]
    public void Closing_after_a_native_return_fences_every_later_creation_step()
    {
        native.Result = _ => { owner.Close("TestClose"); return new(true); };
        var state = new RunProvisioner(owner, native).Run(creator, CancellationToken.None);
        Assert.Equal(new[] { NativeOperation.StagePackage }, native.Calls);
        Assert.True(state.Closing);
        Assert.Equal(CreatorPermit.Relinquished, state.Permit);
    }

    [Fact]
    public void Native_exception_leaves_unknown_inflight_and_recovery_cannot_certify_completion()
    {
        native.Result = _ => throw new IOException("native boundary interrupted");
        Assert.Throws<IOException>(() => new RunProvisioner(owner, native).Run(creator, CancellationToken.None));
        Assert.Single(owner.Snapshot().Pending);
        owner.ObserveExitedWorker(creator);
        native.Result = _ => new(true, ParentAndSubtreeAbsent: true, EnumerationSucceeded: true);
        var state = new RunRecovery(owner, native).RunOnce(recovery, "Recovery");
        Assert.False(state.Retired);
        Assert.Equal("UnknownInFlight", state.IncompleteReason);
    }

    [Fact]
    public void Cancelled_delayed_worker_never_calls_native_and_exact_cleanup_can_retire()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new RunProvisioner(owner, native).Run(creator, cancelled.Token));
        Assert.Empty(native.Calls);
        native.Result = _ => new(true, ParentAndSubtreeAbsent: true, EnumerationSucceeded: true);
        var recoveryLoop = new RunRecovery(owner, native);
        Assert.True(recoveryLoop.RunOnce(recovery, "Cancelled").Retired);
        Assert.True(recoveryLoop.RunOnce(recovery, "DelayedRetry").Retired);
        Assert.Equal(new[] { NativeOperation.RemoveDevice }, native.Calls);
    }

    [Fact]
    public void Three_checkpoint_proof_cannot_retire_until_its_registered_probe_really_stops()
    {
        new RunProvisioner(owner, native).Run(creator, CancellationToken.None);
        owner.ObserveExitedWorker(creator); // injected exact-process observer
        var probe = Worker(WorkerRole.Probe);
        owner.RegisterProbe(probe);
        Assert.True(owner.MarkProofReady());
        foreach (var (step, elapsed) in new[] { (1, 60000), (2, 180000), (3, 300000) })
        {
            clock.Milliseconds = 1000 + elapsed;
            Assert.True(owner.Acknowledge(owner.Manifest.RunId, step));
            Assert.Equal(step == 3, owner.Snapshot().Closing);
        }
        native.Result = _ => new(true, ParentAndSubtreeAbsent: true, EnumerationSucceeded: true);
        Assert.False(new RunRecovery(owner, native).RunOnce(recovery, "ProofFinished").Retired);
        owner.ObserveProbeStopped(probe); // injected retained-process signal, never a release flag
        Assert.True(new RunRecovery(owner, native).RunOnce(recovery, "ProbeJoined").Retired);
        Assert.Equal(7, native.Calls.Count);
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(parent)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }
}
