namespace DeskFuse.DriverTestOwner.Core;

internal sealed class RunState(Guid runId)
{
    private static readonly NativeOperation[] CreationOrder =
    [NativeOperation.StagePackage, NativeOperation.CreateElement, NativeOperation.SetHardwareId,
        NativeOperation.RegisterDevice, NativeOperation.InstallDevice];
    public long Sequence { get; private set; }
    public bool Created { get; private set; }
    public bool Armed { get; private set; }
    public long? ArmedAtMs { get; private set; }
    public bool ProofReady { get; private set; }
    public bool Closing { get; private set; }
    public bool Retired { get; private set; }
    public string? IncompleteReason { get; private set; }
    public bool RebootRequired { get; private set; }
    public CreatorPermit Permit { get; private set; } = CreatorPermit.Available;
    public WorkerIdentity? Creator { get; private set; }
    public WorkerIdentity? Watch { get; private set; }
    public bool CreatorExited { get; private set; }
    public WorkerIdentity? Probe { get; private set; }
    public bool ProbeStopped { get; private set; } = true;
    public Dictionary<Guid, PendingOperation> Pending { get; } = [];
    public HashSet<int> Acknowledged { get; } = [];
    private HashSet<WorkerIdentity> ExitedWorkers { get; } = [];
    public int CreationReturns { get; private set; }
    public long LastCreationReturnSequence { get; private set; }
    public long FinalRemovalIntentSequence { get; private set; }
    public long FinalRemovalReturnSequence { get; private set; }

    public RunSnapshot Snapshot() => new(runId, Sequence, Armed, ProofReady, Closing, Retired,
        IncompleteReason, RebootRequired, Permit, Creator, CreatorExited, Probe, ProbeStopped,
        Pending.Values.ToArray(), Acknowledged.Order().ToArray(), ArmedAtMs,
        LastCreationReturnSequence, FinalRemovalIntentSequence, FinalRemovalReturnSequence, Watch);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public void Apply(long sequence, RunEvent entry, string expectedManifestHash)
    {
        Require(sequence == Sequence + 1, "Nonsequential transition.");
        Require(!Retired, "Retired runs reject every mutation.");
        if (entry.Kind != EventKind.Created) Require(Created, "Run has not been initialized.");
        switch (entry.Kind)
        {
            case EventKind.Created:
                Require(!Created && Sequence == 0 && entry.Read<CreatedData>().ManifestHash == expectedManifestHash,
                    "Manifest identity mismatch.");
                Created = true;
                break;
            case EventKind.Armed:
                var armed = entry.Read<ArmedData>();
                armed.Watch.Validate();
                Require(!Armed && !Closing && armed.AtMs >= 0 && armed.Watch.Role == WorkerRole.Watch,
                    "Invalid recovery arming.");
                Armed = true;
                ArmedAtMs = armed.AtMs;
                Watch = armed.Watch;
                break;
            case EventKind.WorkerRegistered:
                var creator = entry.Read<WorkerIdentity>();
                creator.Validate();
                Require(Armed && !Closing && Creator is null && Permit == CreatorPermit.Available &&
                        creator.Role == WorkerRole.Creator, "Creation permit unavailable.");
                Creator = creator;
                Permit = CreatorPermit.Consumed;
                break;
            case EventKind.OperationIntent:
                var intent = entry.Read<IntentData>();
                intent.Worker.Validate();
                Require(intent.Id != Guid.Empty && Enum.IsDefined(intent.Kind) && !Pending.ContainsKey(intent.Id),
                    "Invalid operation identity.");
                Require(!ExitedWorkers.Contains(intent.Worker), "Exited workers cannot admit work.");
                if (intent.Kind == NativeOperation.RemoveDevice)
                {
                    Require(Closing && IncompleteReason != "OwnershipConflict" && intent.Worker.Role == WorkerRole.Recovery &&
                            Pending.Values.All(p => p.Kind != NativeOperation.RemoveDevice || ExitedWorkers.Contains(p.Worker)),
                        "Removal requires Closing and only one outstanding remove worker.");
                }
                else
                {
                    Require(!Closing && Armed && intent.Worker == Creator && !CreatorExited &&
                            Permit == CreatorPermit.Consumed && CreationReturns < CreationOrder.Length &&
                            intent.Kind == CreationOrder[CreationReturns] &&
                            Pending.Values.All(p => p.Kind == NativeOperation.RemoveDevice),
                        "Creation is closed, out of order or already in flight.");
                }
                Pending.Add(intent.Id, new(intent.Id, intent.Worker, intent.Kind, sequence));
                break;
            case EventKind.OperationReturned:
                var returned = entry.Read<ReturnData>();
                Require(Pending.TryGetValue(returned.Id, out var pending) && pending.Worker == returned.Worker,
                    "Return has no matching worker/intent.");
                Require(!ExitedWorkers.Contains(returned.Worker), "An exited worker cannot acknowledge a call.");
                Pending.Remove(returned.Id);
                RebootRequired |= returned.Result.RebootRequired;
                if (returned.Result.OwnershipConflict)
                {
                    IncompleteReason = "OwnershipConflict";
                    Close("OwnershipConflict");
                }
                if (pending!.Kind != NativeOperation.RemoveDevice)
                {
                    CreationReturns++;
                    LastCreationReturnSequence = sequence;
                    if (!returned.Result.Succeeded || returned.Result.RebootRequired) Close("ProvisioningFailed");
                }
                else if (returned.Result.Succeeded && !returned.Result.RebootRequired &&
                         returned.Result.ParentAndSubtreeAbsent && returned.Result.EnumerationSucceeded)
                {
                    FinalRemovalIntentSequence = pending.IntentSequence;
                    FinalRemovalReturnSequence = sequence;
                }
                break;
            case EventKind.CreatorRelinquished:
                Require(entry.Read<WorkerIdentity>() == Creator && Permit == CreatorPermit.Consumed &&
                        Pending.Values.All(p => p.Worker != Creator), "Creator still has an unresolved call.");
                Permit = CreatorPermit.Relinquished;
                break;
            case EventKind.WorkerExited:
                var exited = entry.Read<WorkerIdentity>();
                exited.Validate();
                Require(exited == Creator || exited == Probe || exited == Watch ||
                        Pending.Values.Any(p => p.Worker == exited), "Exit belongs to a different process incarnation.");
                if (Pending.Values.Any(p => p.Worker == exited))
                {
                    IncompleteReason = "UnknownInFlight";
                    Close("UnknownInFlight");
                }
                ExitedWorkers.Add(exited);
                if (exited == Creator)
                {
                    CreatorExited = true;
                    if (Permit == CreatorPermit.Consumed)
                    {
                        Permit = CreatorPermit.Revoked;
                        Close("CreatorExitedBeforeRelinquishment");
                    }
                }
                if (exited == Probe) { ProbeStopped = true; Close("ProbeExited"); }
                if (exited == Watch) Close("WatchdogExited");
                break;
            case EventKind.ProbeRegistered:
                var probe = entry.Read<WorkerIdentity>();
                probe.Validate();
                Require(probe.Role == WorkerRole.Probe && Probe is null && !Closing && CreatorExited &&
                        Permit == CreatorPermit.Relinquished && CreationReturns == CreationOrder.Length &&
                        !RebootRequired && Pending.Count == 0, "Provisioning has not completed before probe launch.");
                Probe = probe;
                ProbeStopped = false;
                break;
            case EventKind.ProbeStopped:
                Require(entry.Read<WorkerIdentity>() == Probe && Closing, "Wrong probe or missing Closing fence.");
                ProbeStopped = true;
                break;
            case EventKind.ProofReady:
                Require(!Closing && Probe is not null && !ProbeStopped && CreatorExited && !ProofReady,
                    "Proof is not ready.");
                ProofReady = true;
                break;
            case EventKind.Acknowledged:
                var ack = entry.Read<AckData>();
                Require(ProofReady && !Closing && ack.Index is >= 1 and <= 3 &&
                        Acknowledged.Count == ack.Index - 1 && !Acknowledged.Contains(ack.Index),
                    "Invalid or replayed acknowledgement.");
                Acknowledged.Add(ack.Index);
                if (ack.Index == 3) Close("ProofFinished");
                break;
            case EventKind.Closing:
                var reason = entry.Read<ClosingData>().Reason;
                Require(!string.IsNullOrWhiteSpace(reason) && reason.Length <= 256, "Invalid close reason.");
                Close(reason);
                break;
            case EventKind.Retired:
                Require(CanRetire(), "Recovery is not certified complete.");
                Retired = true;
                break;
            default: throw new InvalidDataException("Unknown event kind.");
        }
        Sequence = sequence;
    }

    private void Close(string reason)
    {
        Closing = true;
        if (Permit == CreatorPermit.Available) Permit = CreatorPermit.Revoked;
        if (RebootRequired) IncompleteReason ??= "RebootRequired";
        _ = reason; // Diagnostic reason remains in the immutable event stream.
    }

    public bool CanRetire() => Closing && !Retired &&
        Permit is CreatorPermit.Relinquished or CreatorPermit.Revoked &&
        (Creator is null || CreatorExited) && ProbeStopped && Pending.Count == 0 &&
        IncompleteReason is null && !RebootRequired &&
        FinalRemovalIntentSequence > LastCreationReturnSequence && FinalRemovalReturnSequence > FinalRemovalIntentSequence;
}
