namespace FocusWindow.Core.Input;
/// <summary>
/// Android KeyEvent keyCode -> Windows virtual-key. The client sends BOTH the Linux scancode
/// and the Android keyCode; the keyCode is what is reliable across keyboards and layouts, so
/// it drives the mapping and the scancode is only a fallback. Values are the public constants
/// of android.view.KeyEvent and winuser.h.
/// </summary>
public static class KeyMap
{
    public readonly record struct Key(ushort VirtualKey, bool Extended);
    private static readonly Dictionary<int, Key> Map = Build();
    public static bool TryGet(int androidKeyCode, out Key key) => Map.TryGetValue(androidKeyCode, out key);
    private static Dictionary<int, Key> Build()
    {
        var m = new Dictionary<int, Key>();
        void Add(int android, int vk, bool extended = false) => m[android] = new Key((ushort)vk, extended);
        for (var i = 0; i < 10; i++) Add(7 + i, 0x30 + i);          // KEYCODE_0..9
        for (var i = 0; i < 26; i++) Add(29 + i, 0x41 + i);         // KEYCODE_A..Z
        for (var i = 0; i < 12; i++) Add(131 + i, 0x70 + i);        // KEYCODE_F1..F12
        for (var i = 0; i < 10; i++) Add(144 + i, 0x60 + i);        // KEYCODE_NUMPAD_0..9
        Add(66, 0x0D);        // ENTER
        Add(67, 0x08);        // DEL (backspace)
        Add(61, 0x09);        // TAB
        Add(62, 0x20);        // SPACE
        Add(111, 0x1B);       // ESCAPE
        Add(112, 0x2E, true); // FORWARD_DEL -> VK_DELETE
        Add(124, 0x2D, true); // INSERT
        Add(122, 0x24, true); // MOVE_HOME
        Add(123, 0x23, true); // MOVE_END
        Add(92, 0x21, true);  // PAGE_UP
        Add(93, 0x22, true);  // PAGE_DOWN
        Add(19, 0x26, true);  // DPAD_UP
        Add(20, 0x28, true);  // DPAD_DOWN
        Add(21, 0x25, true);  // DPAD_LEFT
        Add(22, 0x27, true);  // DPAD_RIGHT
        Add(115, 0x14);       // CAPS_LOCK
        Add(116, 0x91);       // SCROLL_LOCK
        Add(143, 0x90);       // NUM_LOCK
        Add(121, 0x13);       // BREAK -> VK_PAUSE
        Add(82, 0x5D, true);  // MENU -> VK_APPS
        Add(59, 0xA0);        // SHIFT_LEFT
        Add(60, 0xA1);        // SHIFT_RIGHT
        Add(113, 0xA2);       // CTRL_LEFT
        Add(114, 0xA3, true); // CTRL_RIGHT
        Add(57, 0xA4);        // ALT_LEFT
        Add(58, 0xA5, true);  // ALT_RIGHT
        Add(117, 0x5B, true); // META_LEFT -> VK_LWIN
        Add(118, 0x5C, true); // META_RIGHT -> VK_RWIN
        Add(55, 0xBC);        // COMMA
        Add(56, 0xBE);        // PERIOD
        Add(68, 0xC0);        // GRAVE
        Add(69, 0xBD);        // MINUS
        Add(70, 0xBB);        // EQUALS
        Add(71, 0xDB);        // LEFT_BRACKET
        Add(72, 0xDD);        // RIGHT_BRACKET
        Add(73, 0xDC);        // BACKSLASH
        Add(74, 0xBA);        // SEMICOLON
        Add(75, 0xDE);        // APOSTROPHE
        Add(76, 0xBF);        // SLASH
        Add(81, 0xBB);        // PLUS -> shares the '=' key
        Add(154, 0x6F, true); // NUMPAD_DIVIDE
        Add(155, 0x6A);       // NUMPAD_MULTIPLY
        Add(156, 0x6D);       // NUMPAD_SUBTRACT
        Add(157, 0x6B);       // NUMPAD_ADD
        Add(158, 0x6E);       // NUMPAD_DOT
        Add(160, 0x0D, true); // NUMPAD_ENTER
        return m;
    }
    /// <summary>Virtual keys for the client's modifier bitmask (Modifiers.Ctrl/Alt/Shift/Meta).</summary>
    public static IEnumerable<Key> ModifierKeys(int mask)
    {
        if ((mask & Protocol.Modifiers.Ctrl) != 0) yield return new Key(0xA2, false);
        if ((mask & Protocol.Modifiers.Alt) != 0) yield return new Key(0xA4, false);
        if ((mask & Protocol.Modifiers.Shift) != 0) yield return new Key(0xA0, false);
        if ((mask & Protocol.Modifiers.Meta) != 0) yield return new Key(0x5B, true);
    }
    public static bool IsModifier(ushort vk) => vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
}
