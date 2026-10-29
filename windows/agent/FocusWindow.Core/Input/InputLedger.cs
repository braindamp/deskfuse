namespace FocusWindow.Core.Input;

/// <summary>Physical identity used by the injector; Unicode units are separately keyed.</summary>
public readonly record struct InputIdentity(string Kind, int Code, int ScanCode = 0, bool Extended = false);
public readonly record struct InputTransition(InputIdentity Identity, bool Down);
public enum InputHeldState { KnownDown, PossiblyDown }

/// <summary>
/// Records intent before insertion. A partial SendInput count is not treated as
/// an accepted-event bitmap; every affected identity remains uncertain.
/// The owner serializes access and never uses this as proof of physical neutrality.
/// </summary>
public sealed class InputLedger
{
    private readonly Dictionary<InputIdentity, InputHeldState> held = [];
    private InputTransition[]? pending;
    public bool HasHeld => held.Count != 0;
    public bool HasPending => pending is not null;
    public bool IsHeld(InputIdentity identity) => held.ContainsKey(identity);
    public bool IsVirtualKeyHeld(int left, int right)
    {
        foreach (var key in held.Keys)
            if (key.Kind == "key" && (key.Code == left || key.Code == right)) return true;
        return false;
    }
    public IReadOnlyDictionary<InputIdentity, InputHeldState> Snapshot() => new Dictionary<InputIdentity, InputHeldState>(held);

    public void Begin(IReadOnlyList<InputTransition> transitions)
    {
        if (pending is not null) throw new InvalidOperationException("An injection attempt is already pending.");
        pending = transitions.ToArray();
    }

    public void Complete(int inserted, int attempted)
    {
        if (pending is null || attempted <= 0 || inserted < 0 || inserted > attempted)
            throw new InvalidOperationException("Invalid injection result.");
        if (inserted == attempted)
            foreach (var transition in pending)
            {
                if (transition.Down) held[transition.Identity] = InputHeldState.KnownDown;
                else held.Remove(transition.Identity);
            }
        else if (inserted != 0)
            foreach (var transition in pending) held[transition.Identity] = InputHeldState.PossiblyDown;
        pending = null;
    }

    // An exception crossing the native boundary cannot prove whether anything
    // was inserted. Retain conservative state instead of fabricating zero.
    public void AbandonUncertain()
    {
        if (pending is null) throw new InvalidOperationException("No pending injection attempt.");
        foreach (var transition in pending) held[transition.Identity] = InputHeldState.PossiblyDown;
        pending = null;
    }
}

/// <summary>
/// One host-wide fence for its input sinks. A local session number protects
/// owner lifetime; it is not the planned authenticated wire generation.
/// </summary>
public sealed class InputSessionArbiter
{
    private readonly object gate = new();
    private readonly InputLedger ledger = new();
    private long owner;
    private bool suspended;

    public long NewSession()
    {
        lock (gate)
        {
            owner = checked(owner + 1);
            suspended = ledger.HasHeld || ledger.HasPending;
            return owner;
        }
    }

    public bool TryExecute(long session, Action<InputLedger> action)
    {
        lock (gate)
        {
            if (session != owner || suspended) return false;
            try { action(ledger); }
            catch
            {
                if (ledger.HasPending) ledger.AbandonUncertain();
                suspended = true;
                throw;
            }
            return true;
        }
    }

    public bool Suspend(long session)
    {
        lock (gate)
        {
            if (session != owner) return false;
            if (ledger.HasHeld || ledger.HasPending) suspended = true;
            return suspended;
        }
    }

    public IReadOnlyDictionary<InputIdentity, InputHeldState> Snapshot()
    {
        lock (gate) return ledger.Snapshot();
    }

    public bool NeedsReconciliation(long session)
    {
        lock (gate) return session == owner && suspended && (ledger.HasHeld || ledger.HasPending);
    }
}
