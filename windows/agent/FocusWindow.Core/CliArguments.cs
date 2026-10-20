using System.Globalization;

namespace FocusWindow.Core;

/// <summary>
/// Shared command-line value parsing for the host agent. Option values may begin
/// with "--" (for example <c>--workspace-args "--new-window"</c>); a known switch
/// immediately after an option is treated as a missing value instead of being
/// consumed, and <c>--name=value</c> removes the ambiguity entirely.
/// </summary>
public static class CliArguments
{
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "-h",
        "--probe-build-id", "--version", "--help",
        "--log", "--port", "--bind", "--fps", "--bitrate",
        "--workspace-app", "--workspace-args",
        "--auto-approve", "--legacy-qr", "--new-pairing", "--single-session",
        "--enable-pc-audio", "--ruler-probe", "--adaptive-dpi",
        "--disable-native-dpi", "--no-open-qr",
        "--experimental-mic-cable", "--phone-mic-output-id", "--phone-mic-device-id",
        "--forget-phone",
        "--width", "--height", "--dpr", "--diag-mm", "--refresh",
        "--out", "--launch", "--hold-until-flag", "--experimental-scale-write",
        "--modes", "--run", "--try-350", "--try-above-cap",
        "--i-accept-virtual-display-test", "--i-accept-unsupported-scale-risk",
    };

    public static string? Value(string[] args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var prefix = name + "=";
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, name, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) return null;
                var next = args[i + 1];
                return KnownOptions.Contains(next) ? null : next;
            }
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
                return arg[prefix.Length..];
        }
        return null;
    }

    public static int Int(string[] args, string name, int fallback) =>
        int.TryParse(Value(args, name), CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static double Double(string[] args, string name, double fallback) =>
        double.TryParse(Value(args, name), CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
