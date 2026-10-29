using System.ComponentModel;
using System.Runtime.InteropServices;
using FocusWindow.Agent.Interop;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Input;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Agent.Input;

internal interface IInputNative
{
    bool GetCursor(out int x, out int y);
    int Metric(int index);
    ushort ScanFor(ushort virtualKey);
    uint Send(Win32Layout.Input[] inputs);
}

internal sealed class WindowsInputNative : IInputNative
{
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    public bool GetCursor(out int x, out int y)
    {
        var success = GetCursorPos(out var point);
        x = point.X; y = point.Y;
        return success;
    }
    public int Metric(int index) => Native.GetSystemMetrics(index);
    public ushort ScanFor(ushort virtualKey) => (ushort)Native.MapVirtualKeyW(virtualKey, 0);
    public uint Send(Win32Layout.Input[] inputs) => Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Layout.Input>());
}
/// <summary>
/// Injects the phone's touch / pen / keyboard with SendInput.
///
/// What this is and is not:
///  * Pointer coordinates are TARGET CLIENT pixels == stream pixels, mapped onto the phone monitor's
///    rectangle (InputGeometry) and normalised to the VIRTUAL desktop. Foreground and window checks
///    do not atomically bind SendInput to a recipient; races and local activity can change delivery.
///  * Pen pressure/tilt are NOT injected: the pen is delivered as a mouse. Real pressure needs the
///    synthetic-pointer-device API (CreateSyntheticPointerDevice), which is not implemented here.
///  * SendInput goes to whatever window has the foreground. Clicking on the phone monitor focuses the
///    window under it, so this is the "dedicated phone mode" focus policy: while you use the phone,
///    your physical keyboard focus follows the phone. That is inherent, and it is documented, not hidden.
///  * An unqualified global release is never sent on focus loss or disconnect.
/// </summary>
internal sealed class SendInputSink : IInputSink
{
    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004,
        MouseRightDown = 0x0008, MouseRightUp = 0x0010, MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040,
        MouseWheel = 0x0800, MouseHWheel = 0x1000, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;
    private const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyUnicode = 0x0004, KeyScanCode = 0x0008;
    private const int WheelDelta = 120;
    private readonly ILog _log;
    private readonly IInputNative _native;
    private readonly InputSessionArbiter _arbiter;
    private readonly long _session;
    private InputLedger _ledger = null!; // Only used while the arbiter lock is held.
    private bool _eventRejected;
    public bool NeedsReconciliation => _arbiter.NeedsReconciliation(_session);
    public SendInputSink(ILog log, InputSessionArbiter arbiter) : this(log, arbiter, new WindowsInputNative()) { }
    internal SendInputSink(ILog log, InputSessionArbiter arbiter, IInputNative native)
    {
        _log = log;
        _native = native;
        _arbiter = arbiter;
        _session = arbiter.NewSession();
        // SendInput silently rejects any cbSize other than sizeof(INPUT): fail fast if the layout is wrong.
        if (Marshal.SizeOf<Win32Layout.Input>() != 40)
            throw new InvalidOperationException("INPUT layout is not 40 bytes; SendInput would reject every call");
    }
    public void Handle(InputEventDto e, InputGeometry geometry) => TryHandle(e, geometry);

    public bool TryHandle(InputEventDto e, InputGeometry geometry)
    {
        var processed = false;
        var admitted = _arbiter.TryExecute(_session, ledger =>
        {
            _ledger = ledger;
            _eventRejected = false;
            switch (e.Kind)
            {
                case "pointer": Pointer(e, geometry); break;
                case "relative": Relative(e, geometry); break;
                case "scroll": Scroll(e, geometry); break;
                case "key": Key(e); break;
                case "text": Text(e.Text); break;
                default: _log.Warn($"unknown input kind '{e.Kind}'"); _eventRejected = true; break;
            }
            processed = !_eventRejected;
        });
        if (!admitted) _log.Warn("input is suspended or belongs to an old session; reconcile held input locally before restarting the host");
        return admitted && processed;
    }
    // ---- pointer -----------------------------------------------------------------------------------
    private void Relative(InputEventDto e, InputGeometry geometry)
    {
        if (!_native.GetCursor(out var cursorX, out var cursorY)) { _eventRejected = true; return; }
        var bounds = geometry.MonitorBounds;
        var x = (int)Math.Clamp((long)cursorX + e.Dx, bounds.X, (long)bounds.Right - 1);
        var y = (int)Math.Clamp((long)cursorY + e.Dy, bounds.Y, (long)bounds.Bottom - 1);
        var (nx, ny) = Normalise(x, y);
        Send([Mouse(nx, ny, 0, MouseMove | MouseAbsolute | MouseVirtualDesk)]);
    }
    private static (uint Down, uint Up) ButtonFlags(string button) => button switch
    {
        "right" => (MouseRightDown, MouseRightUp),
        "middle" => (MouseMiddleDown, MouseMiddleUp),
        _ => (MouseLeftDown, MouseLeftUp),
    };
    private void Pointer(InputEventDto e, InputGeometry geometry)
    {
        var (dx, dy) = geometry.ToDesktop(e.X, e.Y);
        var (nx, ny) = Normalise(dx, dy);
        var move = Mouse(nx, ny, 0, MouseMove | MouseAbsolute | MouseVirtualDesk);
        var (down, up) = ButtonFlags(e.Button);
        var identity = new InputIdentity("button", (int)down);
        switch (e.Action)
        {
            case "down":
                Send([move, Mouse(0, 0, 0, down)], new InputTransition(identity, true));
                break;
            case "up":
                if (!_ledger.IsHeld(identity)) { _log.Warn("unmatched remote button release was dropped"); _eventRejected = true; break; }
                Send([move, Mouse(0, 0, 0, up)], new InputTransition(identity, false));
                break;
            default: // move, hover
                Send([move]);
                break;
        }
    }
    private void Scroll(InputEventDto e, InputGeometry geometry)
    {
        var (dx, dy) = geometry.ToDesktop(e.X, e.Y);
        var (nx, ny) = Normalise(dx, dy);
        var inputs = new List<Win32Layout.Input> { Mouse(nx, ny, 0, MouseMove | MouseAbsolute | MouseVirtualDesk) };
        // The client sends AXIS_VSCROLL * 40: +ve = away from the user, like WHEEL_DELTA. 40 -> 120.
        if (e.Dy != 0) inputs.Add(Mouse(0, 0, (uint)(e.Dy * WheelDelta / 40), MouseWheel));
        if (e.Dx != 0) inputs.Add(Mouse(0, 0, (uint)(e.Dx * WheelDelta / 40), MouseHWheel));
        Send(inputs.ToArray());
    }
    /// <summary>Normalises a physical desktop pixel to SendInput's 0..65535 over the VIRTUAL screen.</summary>
    private (int X, int Y) Normalise(int x, int y)
    {
        var vx = _native.Metric(Native.SmXVirtualScreen);
        var vy = _native.Metric(Native.SmYVirtualScreen);
        var vw = Math.Max(2, _native.Metric(Native.SmCxVirtualScreen));
        var vh = Math.Max(2, _native.Metric(Native.SmCyVirtualScreen));
        var nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }
    // ---- keyboard ----------------------------------------------------------------------------------
    private void Key(InputEventDto e)
    {
        if (!KeyMap.TryGet(e.AndroidKeyCode, out var key))
        {
            // The Linux scancodes for the main block equal PS/2 set-1, so they are a usable fallback.
            if (e.Scancode is > 0 and <= 0x58)
            {
                var scanIdentity = new InputIdentity("scan", 0, e.Scancode);
                if (!e.Down && !_ledger.IsHeld(scanIdentity)) { _log.Warn("unmatched remote scan release was dropped"); _eventRejected = true; return; }
                Send([Keyboard(0, (ushort)e.Scancode, KeyScanCode | (e.Down ? 0u : KeyUp))], new InputTransition(scanIdentity, e.Down));
                return;
            }
            _log.Warn($"no mapping for Android key {e.AndroidKeyCode} (scancode {e.Scancode}); dropped, not guessed");
            _eventRejected = true;
            return;
        }
        var scan = ScanFor(key.VirtualKey);
        var identity = new InputIdentity("key", key.VirtualKey, scan, key.Extended);
        if (!e.Down && !_ledger.IsHeld(identity)) { _log.Warn("unmatched remote key release was dropped"); _eventRejected = true; return; }
        if (KeyMap.IsModifier(key.VirtualKey))
        {
            // A physical modifier arriving on its own: pass it through, track it, and do not also
            // synthesise the sticky-modifier bits around it.
            Send([KeyInput(key, scan, e.Down)], new InputTransition(identity, e.Down));
            return;
        }
        if (e.Down)
        {
            foreach (var mod in KeyMap.ModifierKeys(e.Modifiers))
            {
                var right = mod.VirtualKey switch { 0xA0 => 0xA1, 0xA2 => 0xA3, 0xA4 => 0xA5, 0x5B => 0x5C, _ => 0 };
                var heldModifier = _ledger.IsVirtualKeyHeld(mod.VirtualKey, right);
                if (!heldModifier)
                {
                    _log.Warn("implicit modifier rejected until physical-input attribution and safe cleanup are available");
                    _eventRejected = true;
                    return;
                }
            }
            Send([KeyInput(key, scan, down: true)], new InputTransition(identity, true));
        }
        else
        {
            Send([KeyInput(key, scan, down: false)], new InputTransition(identity, false));
        }
    }
    private ushort ScanFor(ushort vk) => _native.ScanFor(vk);
    private static Win32Layout.Input KeyInput(KeyMap.Key key, ushort scan, bool down) =>
        Keyboard(key.VirtualKey, scan, (key.Extended ? KeyExtended : 0) | (down ? 0u : KeyUp));
    private void Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<Win32Layout.Input>(text.Length * 2);
        var transitions = new List<InputTransition>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(Keyboard(0, ch, KeyUnicode));
            inputs.Add(Keyboard(0, ch, KeyUnicode | KeyUp));
            var identity = new InputIdentity("unicode", ch);
            transitions.Add(new(identity, true));
            transitions.Add(new(identity, false));
        }
        Send(inputs.ToArray(), transitions.ToArray());
    }
    // ---- plumbing ----------------------------------------------------------------------------------
    private static Win32Layout.Input Mouse(int dx, int dy, uint data, uint flags) => new()
    {
        Type = Win32Layout.InputMouse,
        U = new Win32Layout.InputUnion { Mouse = new Win32Layout.MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags } },
    };
    private static Win32Layout.Input Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        Type = Win32Layout.InputKeyboard,
        U = new Win32Layout.InputUnion { Keyboard = new Win32Layout.KeybdInput { Vk = vk, Scan = scan, Flags = flags } },
    };
    private void Send(Win32Layout.Input[] inputs, params InputTransition[] transitions)
    {
        _ledger.Begin(transitions);
        uint sent;
        try { sent = _native.Send(inputs); }
        catch { _ledger.AbandonUncertain(); throw; }
        if (sent > inputs.Length) { _ledger.AbandonUncertain(); throw new InvalidOperationException("Invalid SendInput result."); }
        _ledger.Complete((int)sent, inputs.Length);
        if (sent != inputs.Length)
        {
            // Typically UIPI: the foreground window is elevated and this agent is not.
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"SendInput injected {sent} of {inputs.Length} events. If the foreground app is running as administrator, " +
                "Windows blocks input from a non-elevated agent.");
        }
    }
    public void ReleaseAll()
    {
        if (_arbiter.Suspend(_session))
            _log.Warn("remote held input remains uncertain; no automatic global key or button release was sent");
    }
}
