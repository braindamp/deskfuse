using System.ComponentModel;
using System.Runtime.InteropServices;
using FocusWindow.Agent.Interop;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Input;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Agent.Input;
/// <summary>
/// Injects the phone's touch / pen / keyboard with SendInput.
///
/// What this is and is not:
///  * Pointer coordinates are TARGET CLIENT pixels == stream pixels, mapped onto the phone monitor's
///    rectangle (InputGeometry) and normalised to the VIRTUAL desktop, so a tap lands on the phone
///    monitor and cannot leak onto a physical one.
///  * Pen pressure/tilt are NOT injected: the pen is delivered as a mouse. Real pressure needs the
///    synthetic-pointer-device API (CreateSyntheticPointerDevice), which is not implemented here.
///  * SendInput goes to whatever window has the foreground. Clicking on the phone monitor focuses the
///    window under it, so this is the "dedicated phone mode" focus policy: while you use the phone,
///    your physical keyboard focus follows the phone. That is inherent, and it is documented, not hidden.
///  * Sticky modifiers from the on-screen bar are applied around the key and released afterwards.
/// </summary>
internal sealed class SendInputSink : IInputSink
{
    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004,
        MouseRightDown = 0x0008, MouseRightUp = 0x0010, MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040,
        MouseWheel = 0x0800, MouseHWheel = 0x1000, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;
    private const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyUnicode = 0x0004, KeyScanCode = 0x0008;
    private const int WheelDelta = 120;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _heldButtons = new();
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly HashSet<ushort> _modifiersWePressed = new();
    public SendInputSink(ILog log)
    {
        _log = log;
        // SendInput silently rejects any cbSize other than sizeof(INPUT): fail fast if the layout is wrong.
        if (Marshal.SizeOf<Win32Layout.Input>() != 40)
            throw new InvalidOperationException("INPUT layout is not 40 bytes; SendInput would reject every call");
    }
    public void Handle(InputEventDto e, InputGeometry geometry)
    {
        lock (_gate)
        {
            switch (e.Kind)
            {
                case "pointer": Pointer(e, geometry); break;
                case "relative": Send(Mouse(e.Dx, e.Dy, 0, MouseMove)); break;
                case "scroll": Scroll(e, geometry); break;
                case "key": Key(e); break;
                case "text": Text(e.Text); break;
                default: _log.Warn($"unknown input kind '{e.Kind}'"); break;
            }
        }
    }
    // ---- pointer -----------------------------------------------------------------------------------
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
        switch (e.Action)
        {
            case "down":
                Send(move, Mouse(0, 0, 0, down));
                _heldButtons.Add(e.Button);
                break;
            case "up":
                Send(move, Mouse(0, 0, 0, up));
                _heldButtons.Remove(e.Button);
                break;
            default: // move, hover
                Send(move);
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
    private static (int X, int Y) Normalise(int x, int y)
    {
        var vx = Native.GetSystemMetrics(Native.SmXVirtualScreen);
        var vy = Native.GetSystemMetrics(Native.SmYVirtualScreen);
        var vw = Math.Max(2, Native.GetSystemMetrics(Native.SmCxVirtualScreen));
        var vh = Math.Max(2, Native.GetSystemMetrics(Native.SmCyVirtualScreen));
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
                Send(Keyboard(0, (ushort)e.Scancode, KeyScanCode | (e.Down ? 0u : KeyUp)));
                return;
            }
            _log.Warn($"no mapping for Android key {e.AndroidKeyCode} (scancode {e.Scancode}); dropped, not guessed");
            return;
        }
        if (KeyMap.IsModifier(key.VirtualKey))
        {
            // A physical modifier arriving on its own: pass it through, track it, and do not also
            // synthesise the sticky-modifier bits around it.
            Send(KeyInput(key, e.Down));
            if (e.Down) _heldKeys.Add(key.VirtualKey); else _heldKeys.Remove(key.VirtualKey);
            return;
        }
        if (e.Down)
        {
            foreach (var mod in KeyMap.ModifierKeys(e.Modifiers))
            {
                if (_heldKeys.Contains(mod.VirtualKey)) continue;
                Send(KeyInput(mod, down: true));
                _modifiersWePressed.Add(mod.VirtualKey);
            }
            Send(KeyInput(key, down: true));
            _heldKeys.Add(key.VirtualKey);
        }
        else
        {
            Send(KeyInput(key, down: false));
            _heldKeys.Remove(key.VirtualKey);
            ReleaseModifiersWePressed();
        }
    }
    private void ReleaseModifiersWePressed()
    {
        foreach (var vk in _modifiersWePressed.ToArray())
        {
            Send(Keyboard(vk, ScanFor(vk), KeyUp | (NeedsExtended(vk) ? KeyExtended : 0)));
            _modifiersWePressed.Remove(vk);
        }
    }
    private static bool NeedsExtended(ushort vk) => vk is 0xA3 or 0xA5 or 0x5B or 0x5C;
    private static ushort ScanFor(ushort vk) => (ushort)Native.MapVirtualKeyW(vk, 0 /* MAPVK_VK_TO_VSC */);
    private static Win32Layout.Input KeyInput(KeyMap.Key key, bool down) =>
        Keyboard(key.VirtualKey, ScanFor(key.VirtualKey), (key.Extended ? KeyExtended : 0) | (down ? 0u : KeyUp));
    private void Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<Win32Layout.Input>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(Keyboard(0, ch, KeyUnicode));
            inputs.Add(Keyboard(0, ch, KeyUnicode | KeyUp));
        }
        Send(inputs.ToArray());
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
    private void Send(params Win32Layout.Input[] inputs)
    {
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Layout.Input>());
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
        lock (_gate)
        {
            try
            {
                foreach (var button in _heldButtons.ToArray())
                {
                    Send(Mouse(0, 0, 0, ButtonFlags(button).Up));
                    _heldButtons.Remove(button);
                }
                foreach (var vk in _heldKeys.ToArray())
                {
                    Send(Keyboard(vk, ScanFor(vk), KeyUp | (NeedsExtended(vk) ? KeyExtended : 0)));
                    _heldKeys.Remove(vk);
                }
                ReleaseModifiersWePressed();
            }
            catch (Win32Exception ex)
            {
                _log.Warn("releasing held input: " + ex.Message);
            }
        }
    }
}
