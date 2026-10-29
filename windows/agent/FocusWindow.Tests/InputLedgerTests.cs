using FocusWindow.Core.Input;
using Xunit;

namespace FocusWindow.Tests;

public sealed class InputLedgerTests
{
    private static readonly InputIdentity Ctrl = new("key", 0xA2, 0x1d);
    private static readonly InputIdentity Mouse = new("button", 2);
    private static readonly InputIdentity Scan = new("scan", 0, 0x2a);

    [Fact]
    public void Full_insertion_commits_down_and_up_while_zero_preserves_prior_state()
    {
        var ledger = new InputLedger();
        ledger.Begin([new(Ctrl, true)]);
        Assert.True(ledger.HasPending);
        ledger.Complete(1, 1);
        Assert.Equal(InputHeldState.KnownDown, ledger.Snapshot()[Ctrl]);
        ledger.Begin([new(Ctrl, false)]);
        ledger.Complete(0, 1);
        Assert.Equal(InputHeldState.KnownDown, ledger.Snapshot()[Ctrl]);
        ledger.Begin([new(Ctrl, false)]);
        ledger.Complete(1, 1);
        Assert.False(ledger.HasHeld);
    }

    [Fact]
    public void Partial_result_does_not_invent_a_prefix_bitmap_and_retains_every_affected_identity()
    {
        var ledger = new InputLedger();
        ledger.Begin([new(Ctrl, true), new(Scan, true)]);
        ledger.Complete(1, 2);
        Assert.Equal(InputHeldState.PossiblyDown, ledger.Snapshot()[Ctrl]);
        Assert.Equal(InputHeldState.PossiblyDown, ledger.Snapshot()[Scan]);
    }

    [Fact]
    public void Partial_button_down_after_pointer_move_is_uncertain_and_no_global_release_occurs()
    {
        var ledger = new InputLedger();
        ledger.Begin([new(Mouse, true)]);
        ledger.Complete(1, 2); // Pointer movement plus button down; no accepted-event bitmap.
        Assert.Equal(InputHeldState.PossiblyDown, ledger.Snapshot()[Mouse]);
    }

    [Fact]
    public void Unknown_exception_during_insertion_is_not_fabricated_as_zero()
    {
        var arbiter = new InputSessionArbiter();
        var session = arbiter.NewSession();
        Assert.Throws<InvalidOperationException>(() => arbiter.TryExecute(session, ledger =>
        {
            ledger.Begin([new(Scan, true)]);
            throw new InvalidOperationException("unknown native outcome");
        }));
        Assert.Equal(InputHeldState.PossiblyDown, arbiter.Snapshot()[Scan]);
        Assert.True(arbiter.NeedsReconciliation(session));
        Assert.False(arbiter.TryExecute(session, _ => throw new Exception("must not run")));
    }

    [Fact]
    public void Focus_loss_preserves_dirty_state_and_old_session_cannot_release_or_suspend_new_session()
    {
        var arbiter = new InputSessionArbiter();
        var old = arbiter.NewSession();
        Assert.True(arbiter.TryExecute(old, ledger => { ledger.Begin([new(Ctrl, true)]); ledger.Complete(1, 1); }));
        Assert.True(arbiter.Suspend(old));
        var replacement = arbiter.NewSession();
        Assert.False(arbiter.Suspend(old));
        Assert.False(arbiter.TryExecute(old, _ => throw new Exception("old owner")));
        Assert.False(arbiter.TryExecute(replacement, _ => throw new Exception("dirty state")));
        Assert.True(arbiter.NeedsReconciliation(replacement));
        Assert.Equal(InputHeldState.KnownDown, arbiter.Snapshot()[Ctrl]);
    }

    [Fact]
    public void Stale_owner_cannot_disturb_clean_new_owner()
    {
        var arbiter = new InputSessionArbiter();
        var old = arbiter.NewSession();
        var current = arbiter.NewSession();
        Assert.False(arbiter.Suspend(old));
        Assert.False(arbiter.TryExecute(old, _ => throw new Exception("old owner")));
        Assert.True(arbiter.TryExecute(current, _ => { }));
    }
}
