namespace DeskFuse.DriverTestOwner.Core;

/// <summary>The adapter is constructed for one validated manifest; no caller-supplied target is accepted per operation.</summary>
public interface IDriverNativeOperations
{
    NativeResult Invoke(NativeOperation operation);
    AbsenceObservation ObserveAbsence();
}

/// <summary>
/// Executes the single permitted provisioning attempt. Cancellation is checked
/// between synchronous native calls; it never fabricates a native return or
/// claims the worker process has exited. The independent observer owns that fact.
/// </summary>
public sealed class RunProvisioner(RunCoordinator owner, IDriverNativeOperations native)
{
    private static readonly NativeOperation[] Steps = [NativeOperation.StagePackage, NativeOperation.CreateElement,
        NativeOperation.SetHardwareId, NativeOperation.RegisterDevice, NativeOperation.InstallDevice];

    public RunSnapshot Run(WorkerIdentity worker, CancellationToken cancellationToken)
    {
        worker.Validate();
        if (worker.Role != WorkerRole.Creator) throw new InvalidOperationException("A creator worker is required.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.RegisterCreator(worker);
            foreach (var step in Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.Snapshot().Closing) break;
                var result = owner.Execute(worker, step, _ => native.Invoke(step));
                if (!result.Succeeded || result.RebootRequired || result.OwnershipConflict) break;
            }
        }
        catch
        {
            owner.Close("ProvisionerInterrupted");
            throw;
        }
        finally
        {
            var state = owner.Snapshot();
            // A thrown native call leaves its durable intent unresolved. Only
            // an actual native return permits relinquishment; never hide it.
            if (state.Creator == worker && state.Permit == CreatorPermit.Consumed &&
                state.Pending.All(p => p.Worker != worker)) owner.RelinquishCreator(worker);
        }
        return owner.Snapshot();
    }
}

/// <summary>
/// One scoped cleanup attempt. It can be used for best-effort emergency removal
/// while creation is still in flight, but only the coordinator's full retirement
/// predicate can certify completion. No package/trust validation is needed here.
/// </summary>
public sealed class RunRecovery(RunCoordinator owner, IDriverNativeOperations native)
{
    public RunSnapshot RunOnce(WorkerIdentity worker, string reason)
    {
        worker.Validate();
        if (worker.Role != WorkerRole.Recovery) throw new InvalidOperationException("A recovery worker is required.");
        if (owner.Snapshot().Retired) return owner.Snapshot();
        owner.Close(reason);
        owner.Execute(worker, NativeOperation.RemoveDevice, _ => native.Invoke(NativeOperation.RemoveDevice));
        owner.TryRetire(_ => native.ObserveAbsence());
        return owner.Snapshot();
    }
}
