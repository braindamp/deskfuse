using System.Text.Json.Nodes;
namespace FocusWindow.Core.Protocol;
/// <summary>
/// Mirror of the Android client's PhoneProfile (android/.../profile/PhoneProfile.kt).
/// FR-DPI-01: the ONLY thing the host learns about the phone. Field names and enum
/// spellings match the Kotlin side exactly (the nested enums have no @SerialName, so they
/// travel as their constant names).
/// </summary>
public sealed record PhoneProfile(
    int Version,
    string DeviceId,
    string DeviceLabel,
    int PhysicalW,
    int PhysicalH,
    float Dpr,
    int SafeTop,
    int SafeBottom,
    int SafeLeft,
    int SafeRight,
    float FontScale,
    int MaxRefreshHz,
    int DiagonalMm,
    string Orientation,   // "PORTRAIT" | "LANDSCAPE"
    string FormFactor)    // "PHONE" | "FOLDABLE_INNER" | "FOLDABLE_COVER" | "TABLET"
{
    public int SurfaceW => PhysicalW - SafeLeft - SafeRight;
    public int SurfaceH => PhysicalH - SafeTop - SafeBottom;
    /// <summary>The logical canvas the phone's own maths expects: pixels / DPR.</summary>
    public double CanvasDpW => PhysicalW / (double)Dpr;
    public double CanvasDpH => PhysicalH / (double)Dpr;
    /// <summary>Percent the client would have liked (DPR x 100), clamped to Windows' 100..500.</summary>
    public int RequestedScalePercent => Math.Clamp((int)Math.Round(Dpr * 100.0), 100, 500);
    /// <summary>The DPI an app should see at the phone's exact density (96 x DPR).</summary>
    public int ExpectedDpi => (int)Math.Round(96.0 * Dpr);
    public string Validate()
    {
        if (PhysicalW <= 0 || PhysicalH <= 0) return "physical size must be positive";
        if (!float.IsFinite(Dpr) || Dpr < 1.0f || Dpr > 5.0f)
            return $"missing or implausible phone DPR {Dpr}; calibration requires the Android device's measured density";
        if (MaxRefreshHz < 24) return $"implausible refresh {MaxRefreshHz}";
        if (DiagonalMm is < 60 or > 500) return $"implausible diagonal {DiagonalMm} mm";
        return "";
    }
    public static PhoneProfile Parse(JsonObject o) => new(
        Version: o["version"]!.GetValue<int>(),
        DeviceId: o["deviceId"]!.GetValue<string>(),
        DeviceLabel: o["deviceLabel"]!.GetValue<string>(),
        PhysicalW: o["physicalW"]!.GetValue<int>(),
        PhysicalH: o["physicalH"]!.GetValue<int>(),
        Dpr: o["dpr"]!.GetValue<float>(),
        SafeTop: o["safeTop"]!.GetValue<int>(),
        SafeBottom: o["safeBottom"]!.GetValue<int>(),
        SafeLeft: o["safeLeft"]!.GetValue<int>(),
        SafeRight: o["safeRight"]!.GetValue<int>(),
        FontScale: o["fontScale"]!.GetValue<float>(),
        MaxRefreshHz: o["maxRefreshHz"]!.GetValue<int>(),
        DiagonalMm: o["diagonalMm"]!.GetValue<int>(),
        Orientation: o["orientation"]!.GetValue<string>(),
        FormFactor: o["formFactor"]!.GetValue<string>());
}
public enum OccupantKind { Canvas, Clone, Window, Session }
public enum DensityMethod { AlreadyCorrect, IddScale, PerMonitorOverride, FallbackScaled }
public enum StateModel { SharedView, Coauthored, ReadOnlyClone, SaveBack }
/// <summary>
/// Mirror of the client's TargetRecord. Every frame references <see cref="Version"/>; the
/// client drops frames of a version it has not seen (FR-ENC-05), so the host sends the
/// Target BEFORE the first frame of a new version and opens it with a keyframe.
/// </summary>
public sealed record TargetRecord(
    long Version,
    OccupantKind Occupant,
    int WidthPx,
    int HeightPx,
    int Dpi,
    int CanvasDpW,
    int CanvasDpH,
    bool Native,
    DensityMethod Method,
    string Codec,
    string PixelFormat,
    int RefreshHz,
    string? DegradedReason,
    StateModel State,
    string AppLabel,
    string? DocumentLabel,
    string? ColorRange = null,
    string? SourceId = null)
{
    public JsonObject ToJson() => new()
    {
        ["version"] = Version,
        ["occupant"] = Occupant switch
        {
            OccupantKind.Canvas => "canvas",
            OccupantKind.Clone => "clone",
            OccupantKind.Window => "window",
            _ => "session",
        },
        ["widthPx"] = WidthPx,
        ["heightPx"] = HeightPx,
        ["dpi"] = Dpi,
        ["canvasDpW"] = CanvasDpW,
        ["canvasDpH"] = CanvasDpH,
        ["native"] = Native,
        ["densityMethod"] = Method switch
        {
            DensityMethod.AlreadyCorrect => "already_correct",
            DensityMethod.IddScale => "iddscale",
            DensityMethod.PerMonitorOverride => "permonitor_override",
            _ => "fallback_scaled",
        },
        ["codec"] = Codec,
        ["pixelFormat"] = PixelFormat,
        ["refreshHz"] = RefreshHz,
        ["degradedReason"] = DegradedReason,
        ["stateModel"] = State switch
        {
            StateModel.SharedView => "shared_view",
            StateModel.Coauthored => "coauthored",
            StateModel.ReadOnlyClone => "read_only_clone",
            _ => "save_back",
        },
        ["appLabel"] = AppLabel,
        ["documentLabel"] = DocumentLabel,
        ["colorRange"] = ColorRange,
        ["sourceId"] = SourceId,
    };
}
/// <summary>Client input. Coordinates are in TARGET CLIENT PIXELS (== stream pixels).</summary>
public sealed record InputEventDto(
    string Kind,                 // pointer | relative | scroll | key | text
    int X = 0,
    int Y = 0,
    int Dx = 0,
    int Dy = 0,
    string Action = "",          // down | move | up | hover
    string Button = "left",      // left | right | middle
    float Pressure = 0,
    float TiltX = 0,
    float TiltY = 0,
    bool IsStylus = false,
    int Scancode = 0,
    int AndroidKeyCode = 0,
    bool Down = false,
    int Modifiers = 0,
    int Repeat = 0,
    string? Text = null)
{
    public static InputEventDto Parse(JsonObject o)
    {
        var kind = o["type"]!.GetValue<string>();
        return new InputEventDto(
            Kind: kind,
            X: o["x"]?.GetValue<int>() ?? 0,
            Y: o["y"]?.GetValue<int>() ?? 0,
            Dx: o["dx"]?.GetValue<int>() ?? 0,
            Dy: o["dy"]?.GetValue<int>() ?? 0,
            Action: o["action"]?.GetValue<string>() ?? "",
            Button: o["button"]?.GetValue<string>() ?? "left",
            Pressure: o["pressure"]?.GetValue<float>() ?? 0f,
            TiltX: o["tiltX"]?.GetValue<float>() ?? 0f,
            TiltY: o["tiltY"]?.GetValue<float>() ?? 0f,
            IsStylus: o["isStylus"]?.GetValue<bool>() ?? false,
            Scancode: o["scancode"]?.GetValue<int>() ?? 0,
            AndroidKeyCode: o["keyCode"]?.GetValue<int>() ?? 0,
            Down: o["down"]?.GetValue<bool>() ?? false,
            Modifiers: o["modifiers"]?.GetValue<int>() ?? 0,
            Repeat: o["repeat"]?.GetValue<int>() ?? 0,
            Text: o["text"]?.GetValue<string>());
    }
}
/// <summary>Modifier bit flags, identical to the client's Modifiers object.</summary>
public static class Modifiers
{
    public const int None = 0;
    public const int Ctrl = 1 << 0;
    public const int Alt = 1 << 1;
    public const int Shift = 1 << 2;
    public const int Meta = 1 << 3;
}
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public override string ToString() => $"{Width}x{Height}@({X},{Y})";
}
