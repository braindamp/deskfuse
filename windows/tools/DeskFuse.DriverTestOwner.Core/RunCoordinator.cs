namespace DeskFuse.DriverTestOwner.Core;

public interface IDriverTestClock { long Milliseconds { get; } }

/// <summary>Production transition authority; the platform adapter supplies observations, never peer booleans.</summary>
public sealed class RunCoordinator
{
    private readonly RunJournal journal;
    private readonly SafetyPolicy policy;
    private readonly IDriverTestClock clock;
    private readonly IPackageAdmissionVerifier? packageVerifier;
    public RunManifest Manifest => journal.Manifest;

    public RunCoordinator(RunJournal journal, byte[] policyBytes, IDriverTestClock clock,
        IPackageAdmissionVerifier? packageVerifier = null)
    {
        if (ContractJson.Hash(policyBytes) != journal.Manifest.PolicySha256)
            throw new InvalidDataException("Safety policy differs from the run manifest.");
        var policy = ContractJson.Read<SafetyPolicy>(policyBytes);
        policy.Validate();
        this.journal = journal;
        this.policy = policy with { PromptAtMs = policy.PromptAtMs.ToArray(), AckDeadlineMs = policy.AckDeadlineMs.ToArray() };
        this.clock = clock;
        this.packageVerifier = packageVerifier;
    }

    public RunSnapshot Snapshot() => journal.Snapshot();

    public void Arm(RecoveryConfirmation confirmation)
    {
        if (confirmation.RunId != Manifest.RunId || confirmation.BootTask != Manifest.BootTask ||
            !confirmation.TaskIdentityVerified || !confirmation.ProtectedBundleVerified)
            throw new InvalidOperationException("Recovery identity/protection was not verified.");
        Commit(RunEvent.Of(EventKind.Armed, new ArmedData(clock.Milliseconds, confirmation.Watch)));
    }

    public void RegisterCreator(WorkerIdentity worker)
    {
        var snapshot = Snapshot();
        if (!snapshot.Armed || snapshot.Closing || snapshot.Retired)
            throw new InvalidOperationException("Recovery is not armed for creation.");
        using var custody = RequirePackageAdmission(null);
        CommitWithAdmission(custody, () => { Commit(RunEvent.Of(EventKind.WorkerRegistered, worker)); return 0; });
    }
    public void RelinquishCreator(WorkerIdentity worker) => Commit(RunEvent.Of(EventKind.CreatorRelinquished, worker));
    public void ObserveExitedWorker(WorkerIdentity observedIdentity) => Commit(RunEvent.Of(EventKind.WorkerExited, observedIdentity));
    public void RegisterProbe(WorkerIdentity worker) => Commit(RunEvent.Of(EventKind.ProbeRegistered, worker));
    public void ObserveProbeStopped(WorkerIdentity observedIdentity) => Commit(RunEvent.Of(EventKind.ProbeStopped, observedIdentity));
    public bool MarkProofReady()
    {
        return journal.Update(state =>
        {
            var elapsed = Elapsed(state);
            if (elapsed < 0 || elapsed >= policy.ProofReadyDeadlineMs)
                return (RunEvent.Of(EventKind.Closing, new ClosingData("ProofReadyDeadline")), false);
            return (RunEvent.Of(EventKind.ProofReady, 0), true);
        });
    }

    public Guid Admit(WorkerIdentity worker, NativeOperation kind)
    {
        if (kind != NativeOperation.RemoveDevice)
            throw new InvalidOperationException("Creation requires package custody and a fresh admission fence.");
        return AdmitIntent(worker, kind);
    }

    private Guid AdmitIntent(WorkerIdentity worker, NativeOperation kind)
    {
        var id = journal.Update(state =>
        {
            if (kind != NativeOperation.RemoveDevice && state.Armed && !state.Closing && !state.Retired)
            {
                var elapsed = Elapsed(state);
                if (elapsed < 0 || elapsed >= policy.ProofReadyDeadlineMs)
                    return (RunEvent.Of(EventKind.Closing, new ClosingData("ProvisioningDeadline")), Guid.Empty);
            }
            var operationId = Guid.NewGuid();
            return (RunEvent.Of(EventKind.OperationIntent, new IntentData(operationId, worker, kind)), operationId);
        });
        if (id == Guid.Empty) throw new InvalidOperationException("Provisioning deadline expired before native admission.");
        return id;
    }

    // The caller must invoke this only after the real native call returned. Do
    // not catch a process crash/cancellation and fabricate a matching return.
    public void Returned(WorkerIdentity worker, Guid operationId, NativeResult result)
    {
        var creation = journal.Read(state => state.Pending.TryGetValue(operationId, out var pending) &&
            pending.Kind != NativeOperation.RemoveDevice);
        if (creation) throw new InvalidOperationException("Creation returns require retained custody.");
        RecordReturn(worker, operationId, result);
    }

    private void RecordReturn(WorkerIdentity worker, Guid operationId, NativeResult result) =>
        Commit(RunEvent.Of(EventKind.OperationReturned, new ReturnData(operationId, worker, result)));

    public NativeResult Execute(WorkerIdentity worker, NativeOperation kind, Func<RunManifest, NativeResult> native)
    {
        if (kind != NativeOperation.RemoveDevice)
        {
            using var admitted = BeginPackageOperation(worker, kind);
            var result = native(Manifest);
            admitted.Returned(result);
            return result;
        }
        var id = AdmitIntent(worker, kind); // Append + Flush(true) precede the callback.
        var otherResult = native(Manifest);
        Returned(worker, id, otherResult);
        return otherResult;
    }

    public PackageOperation BeginPackageOperation(WorkerIdentity worker, NativeOperation kind)
    {
        if (kind == NativeOperation.RemoveDevice || !Enum.IsDefined(kind))
            throw new InvalidOperationException("Not a creation operation.");
        var custody = RequirePackageAdmission(kind);
        try { return new PackageOperation(this, worker, CommitWithAdmission(custody, () => AdmitIntent(worker, kind)), custody); }
        catch { custody.Dispose(); throw; }
    }

    public sealed class PackageOperation : IDisposable
    {
        private readonly RunCoordinator owner;
        private readonly WorkerIdentity worker;
        private IDisposable? custody;
        private bool returned;
        public Guid Id { get; }

        internal PackageOperation(RunCoordinator owner, WorkerIdentity worker, Guid id, IDisposable custody)
            => (this.owner, this.worker, Id, this.custody) = (owner, worker, id, custody);

        public void Returned(NativeResult result)
        {
            if (custody is null || returned) throw new InvalidOperationException("Package operation is already complete.");
            owner.RecordReturn(worker, Id, result);
            returned = true;
        }

        // An exception or worker exit never invents a native return. Recovery
        // observes the durable outstanding intent and handles it as uncertain.
        public void Dispose() => Interlocked.Exchange(ref custody, null)?.Dispose();
    }

    public void Close(string reason) => journal.Update(state => state.Retired || state.Closing
        ? ((RunEvent?)null, 0)
        : (RunEvent.Of(EventKind.Closing, new ClosingData(reason)), 0));

    public bool Acknowledge(Guid runId, int index)
    {
        if (runId != Manifest.RunId) return false; // Stale input cannot close another run.
        return journal.Update(state =>
        {
            if (state.Closing || state.Retired) return ((RunEvent?)null, false);
            var elapsed = Elapsed(state);
            if (index is < 1 or > 3 || state.Acknowledged.Count != index - 1 ||
                !state.ProofReady || elapsed < policy.PromptAtMs[index - 1] || elapsed >= policy.AckDeadlineMs[index - 1])
                return (RunEvent.Of(EventKind.Closing, new ClosingData("InvalidAcknowledgement")), false);
            return (RunEvent.Of(EventKind.Acknowledged, new AckData(index, elapsed)), true);
        });
    }

    public void CheckHealth(long lastRunnerHeartbeatMs, long lastWatchHeartbeatMs)
    {
        journal.Update(state =>
        {
            if (!state.Armed || state.Closing || state.Retired) return ((RunEvent?)null, 0);
            var now = clock.Milliseconds;
            var elapsed = Elapsed(state);
            string? reason = null;
            if (elapsed < 0 || lastRunnerHeartbeatMs > now || lastWatchHeartbeatMs > now ||
                lastRunnerHeartbeatMs < state.ArmedAtMs || lastWatchHeartbeatMs < state.ArmedAtMs ||
                now - lastRunnerHeartbeatMs >= policy.HeartbeatTimeoutMs ||
                now - lastWatchHeartbeatMs >= policy.HeartbeatTimeoutMs) reason = "HeartbeatLost";
            else if (elapsed >= policy.CloseAtMs) reason = "ProofHardClose";
            else if (!state.ProofReady && elapsed >= policy.ProofReadyDeadlineMs) reason = "ProofReadyDeadline";
            else if (Enumerable.Range(0, 3).Any(i => elapsed >= policy.AckDeadlineMs[i] && !state.Acknowledged.Contains(i + 1)))
                reason = "AcknowledgementDeadline";
            return reason is null ? ((RunEvent?)null, 0) : (RunEvent.Of(EventKind.Closing, new ClosingData(reason)), 0);
        });
    }

    public bool TryRetire(Func<RunManifest, AbsenceObservation> observe)
    {
        var expected = journal.Read(state => (state.Sequence, state.CanRetire()));
        if (!expected.Item2) return false;
        // Potentially blocking OS enumeration is outside the state lock.
        var observed = observe(Manifest);
        if (observed.RunId != Manifest.RunId ||
            !string.Equals(observed.InstanceId, Manifest.InstanceId, StringComparison.OrdinalIgnoreCase) ||
            !observed.ParentAndSubtreeAbsent || !observed.EnumerationSucceeded) return false;
        return journal.Update(state => state.Sequence == expected.Sequence && state.CanRetire()
            ? (RunEvent.Of(EventKind.Retired, 0), true) : ((RunEvent?)null, false));
    }

    private long Elapsed(RunState state) => state.ArmedAtMs is long start
        ? checked(clock.Milliseconds - start) : throw new InvalidOperationException("Recovery is not armed.");
    private void Commit(RunEvent entry) => journal.Update(_ => (entry, 0));

    private IPackageAdmissionLease RequirePackageAdmission(NativeOperation? operation)
    {
        try
        {
            if (Manifest.Version != 2 || Manifest.PackageAdmission is null || packageVerifier is null)
                throw new InvalidOperationException("Live package admission is unavailable.");
            Manifest.PackageAdmission.Validate();
            return packageVerifier.AcquireForCreation(Manifest, operation)
                ?? throw new InvalidOperationException("Package verifier did not retain custody.");
        }
        catch
        {
            Close("PackageAdmissionFailed");
            throw;
        }
    }

    private T CommitWithAdmission<T>(IPackageAdmissionLease custody, Func<T> commit)
    {
        try { return custody.CommitAdmission(commit); }
        catch { Close("PackageAdmissionFailed"); throw; }
    }
}
