namespace DeskFuse.DriverTestOwner.Core;

/// <summary>Bounds remove workers and avoids repeating successful emergency removal until quiescence changes.</summary>
public sealed class RecoveryAttemptSchedule
{
    private (long CreationReturn, bool CreatorExited, bool ProbeStopped)? lastFrontier;
    private long lastRemovalReturn, nextAt;

    public bool ShouldStart(RunSnapshot state, bool previousWorkerExited, long nowMs)
    {
        if (!state.Closing || state.Retired || !state.ProbeStopped || state.IncompleteReason == "OwnershipConflict" ||
            !previousWorkerExited || nowMs < nextAt || state.Pending.Any(p => p.Kind == NativeOperation.RemoveDevice))
            return false;
        // A dead remove process with an unresolved native intent is still
        // unknown work. Do not pile up new removal attempts behind it.
        return lastFrontier != Frontier(state) || state.FinalRemovalReturnSequence <= lastRemovalReturn;
    }

    public void Starting(RunSnapshot state, long nowMs)
    {
        if (!ShouldStart(state, true, nowMs)) throw new InvalidOperationException("A removal attempt is not due.");
        lastFrontier = Frontier(state);
        lastRemovalReturn = state.FinalRemovalReturnSequence;
        nextAt = checked(nowMs + 3000);
    }

    private static (long, bool, bool) Frontier(RunSnapshot state) =>
        (state.LastCreationReturnSequence, state.CreatorExited, state.ProbeStopped);
}
