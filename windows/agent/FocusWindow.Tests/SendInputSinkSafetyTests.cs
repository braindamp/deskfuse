using System.ComponentModel;
using FocusWindow.Agent.Input;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Input;
using FocusWindow.Core.Protocol;
using Xunit;

namespace FocusWindow.Tests;

public sealed class SendInputSinkSafetyTests
{
    private sealed class Native : IInputNative
    {
        public readonly List<Win32Layout.Input[]> Calls = [];
        public readonly Queue<uint> Results = [];
        public bool GetCursor(out int x, out int y) { x = 20; y = 20; return true; }
        public int Metric(int index) => index is 78 or 79 ? 1000 : 0;
        public ushort ScanFor(ushort virtualKey) => virtualKey;
        public uint Send(Win32Layout.Input[] inputs)
        {
            Calls.Add(inputs);
            return Results.Count == 0 ? (uint)inputs.Length : Results.Dequeue();
        }
    }

    private static readonly InputGeometry Geometry = new(new RectI(0, 0, 100, 100), 100, 100);

    [Fact]
    public void Partial_pointer_insertion_suspends_and_disconnect_never_injects_a_global_up()
    {
        var native = new Native();
        native.Results.Enqueue(1); // one of move + down; no inference about which one
        var arbiter = new InputSessionArbiter();
        var sink = new SendInputSink(NullLog.Instance, arbiter, native);
        Assert.Throws<Win32Exception>(() => sink.TryHandle(new("pointer", Action: "down"), Geometry));
        Assert.True(sink.NeedsReconciliation);
        sink.ReleaseAll();
        Assert.Single(native.Calls);
        Assert.Equal(InputHeldState.PossiblyDown, Assert.Single(arbiter.Snapshot()).Value);
        Assert.False(sink.TryHandle(new("pointer", Action: "up"), Geometry));
        Assert.Single(native.Calls);
    }

    [Fact]
    public void Fallback_scancode_is_tracked_and_an_unmatched_up_is_never_injected()
    {
        var native = new Native();
        var sink = new SendInputSink(NullLog.Instance, new(), native);
        Assert.False(sink.TryHandle(new("key", Scancode: 30, AndroidKeyCode: 9999), Geometry));
        Assert.Empty(native.Calls);
        Assert.True(sink.TryHandle(new("key", Scancode: 30, AndroidKeyCode: 9999, Down: true), Geometry));
        Assert.True(sink.TryHandle(new("key", Scancode: 30, AndroidKeyCode: 9999, Down: false), Geometry));
        Assert.Equal(2, native.Calls.Count);
        sink.ReleaseAll();
        Assert.Equal(2, native.Calls.Count);
    }

    [Fact]
    public void Zero_insertion_preserves_existing_hold_and_requires_reconciliation()
    {
        var native = new Native();
        var arbiter = new InputSessionArbiter();
        var sink = new SendInputSink(NullLog.Instance, arbiter, native);
        Assert.True(sink.TryHandle(new("key", AndroidKeyCode: 29, Down: true), Geometry));
        native.Results.Enqueue(0);
        Assert.Throws<Win32Exception>(() => sink.TryHandle(new("key", AndroidKeyCode: 29), Geometry));
        Assert.Equal(InputHeldState.KnownDown, Assert.Single(arbiter.Snapshot()).Value);
        Assert.True(sink.NeedsReconciliation);
        sink.ReleaseAll();
        Assert.Equal(2, native.Calls.Count);
    }

    [Fact]
    public void Stale_session_neither_injects_nor_releases_new_session_input()
    {
        var native = new Native();
        var arbiter = new InputSessionArbiter();
        var old = new SendInputSink(NullLog.Instance, arbiter, native);
        var current = new SendInputSink(NullLog.Instance, arbiter, native);
        Assert.False(old.TryHandle(new("key", AndroidKeyCode: 29, Down: true), Geometry));
        old.ReleaseAll();
        Assert.True(current.TryHandle(new("key", AndroidKeyCode: 29, Down: true), Geometry));
        old.ReleaseAll();
        Assert.True(current.TryHandle(new("key", AndroidKeyCode: 29), Geometry));
        Assert.Equal(2, native.Calls.Count);
    }

    [Fact]
    public void Implicit_modifiers_are_not_synthesized_but_explicit_remote_modifiers_work()
    {
        var native = new Native();
        var sink = new SendInputSink(NullLog.Instance, new(), native);
        Assert.False(sink.TryHandle(new("key", AndroidKeyCode: 29, Down: true, Modifiers: Modifiers.Ctrl), Geometry));
        Assert.Empty(native.Calls);
        Assert.True(sink.TryHandle(new("key", AndroidKeyCode: 113, Down: true), Geometry));
        Assert.True(sink.TryHandle(new("key", AndroidKeyCode: 29, Down: true, Modifiers: Modifiers.Ctrl), Geometry));
        Assert.True(sink.TryHandle(new("key", AndroidKeyCode: 29, Modifiers: Modifiers.Ctrl), Geometry));
        Assert.True(sink.TryHandle(new("key", AndroidKeyCode: 113), Geometry));
        Assert.Equal(4, native.Calls.Count);
    }
}
