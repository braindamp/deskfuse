namespace FocusWindow.Core.Session;

/// <summary>
/// Roles are additive device contributions, not mutually exclusive device types.
/// Requested means user/device intent, NOT actual Windows endpoint availability.
/// No capture, input or display permission follows from a claim alone.
/// </summary>
public static class DeviceCapabilities
{
    public const string Display = "display";
    public const string Camera = "camera";
    public const string Microphone = "microphone";
    public const string Speakers = "speakers";
    public const string Touchpad = "touchpad";
    public const string Keyboard = "keyboard";
    public const string Sensors = "sensors";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Display, Camera, Microphone, Speakers, Touchpad, Keyboard, Sensors,
    };

    public static IReadOnlyList<string> Validate(IEnumerable<string>? requested, bool accessoryOnly)
    {
        if (requested is null) return Array.Empty<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in requested)
        {
            if (capability is null || !Known.Contains(capability))
                throw new ArgumentException("Unknown device contribution");
            if (accessoryOnly && capability == Display)
                throw new ArgumentException("An accessory connection cannot acquire a display");
            if (!distinct.Add(capability)) continue;
            if (distinct.Count > Known.Count) throw new ArgumentException("Too many capabilities");
        }
        return distinct.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// A capability only becomes active when a real feature provider confirms
    /// that it is ready, independently of the peer's declared intentions.
    /// At present the accessory host implements no active device providers.
    /// </summary>
    public static IReadOnlyList<string> ActiveForAccessory => Array.Empty<string>();
}
