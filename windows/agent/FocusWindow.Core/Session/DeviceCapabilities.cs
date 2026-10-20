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
    /// The capabilities that are actually active. A capability is only
    /// active when a real Windows provider confirmed that it is ready; a
    /// request alone never makes a device appear.
    /// </summary>
    public static IReadOnlyList<string> ActiveForAccessory(IEnumerable<string>? active = null) =>
        (active ?? Array.Empty<string>())
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();
}
