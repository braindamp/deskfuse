using System.Globalization;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Core.Display;
/// <summary>
/// The scale steps the Windows Settings UI actually offers. Windows does not accept an
/// arbitrary percentage per monitor: 262.5% (Android DPR 2.625, extremely common) simply
/// does not exist, and a request must snap to one of these.
/// </summary>
public static class WindowsScale
{
    public static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };
    public static int DpiFor(int percent) => (int)Math.Round(96.0 * percent / 100.0);
    public static int IndexOf(int percent) => Array.IndexOf(Steps, percent);
    /// <summary>Nearest step in log space (so 2.0x vs 3.0x is symmetric); ties go to the LARGER step.</summary>
    public static int NearestStep(double percent)
    {
        var best = Steps[0];
        var bestDistance = double.MaxValue;
        foreach (var step in Steps)
        {
            var distance = Math.Abs(Math.Log(step / percent));
            if (distance < bestDistance - 1e-12 || (Math.Abs(distance - bestDistance) <= 1e-12 && step > best))
            {
                best = step;
                bestDistance = distance;
            }
        }
        return best;
    }
    public static int LargestStepAtMost(int percent)
    {
        var result = Steps[0];
        foreach (var step in Steps) if (step <= percent) result = step;
        return result;
    }
    public static int SmallestStepAtLeast(int percent)
    {
        foreach (var step in Steps) if (step >= percent) return step;
        return Steps[^1];
    }
}
/// <summary>
/// What Windows says about one monitor's scale: the value it recommends (derived from the
/// EDID physical size + resolution), the value currently in force, and the largest value it
/// will allow. Read through the undocumented DisplayConfigGetDeviceInfo packet (type -3),
/// which reports them as indices RELATIVE to the recommended step.
/// </summary>
public sealed record ScaleCaps(int RecommendedPercent, int CurrentPercent, int MaxPercent)
{
    public int MinPercent => WindowsScale.Steps[0];
    /// <summary>
    /// Builds caps from the raw (min, cur, max) relative indices. Returns null if they do not
    /// land inside the known step table — the signal that Windows changed the packet's
    /// meaning, in which case the caller must NOT guess.
    /// </summary>
    public static ScaleCaps? FromRelative(int minRel, int curRel, int maxRel)
    {
        var recommendedIndex = Math.Abs(minRel);
        var steps = WindowsScale.Steps;
        if (recommendedIndex >= steps.Length) return null;
        if (recommendedIndex + curRel < 0 || recommendedIndex + curRel >= steps.Length) return null;
        if (recommendedIndex + maxRel < 0 || recommendedIndex + maxRel >= steps.Length) return null;
        return new ScaleCaps(steps[recommendedIndex], steps[recommendedIndex + curRel], steps[recommendedIndex + maxRel]);
    }
    /// <summary>The relative index the SET packet wants for an absolute percentage.</summary>
    public int RelativeFor(int percent)
    {
        var index = WindowsScale.IndexOf(percent);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(percent), $"{percent}% is not a Windows scale step");
        if (percent > MaxPercent) throw new ArgumentOutOfRangeException(nameof(percent), $"{percent}% exceeds this monitor's cap of {MaxPercent}%");
        return index - WindowsScale.IndexOf(RecommendedPercent);
    }
    public bool Allows(int percent) => WindowsScale.IndexOf(percent) >= 0 && percent <= MaxPercent;
}
public enum PlanKind
{
    /// <summary>A Windows step equals the phone's DPR within 1%: pixels 1:1 AND density exact.</summary>
    Exact,
    /// <summary>Pixels stay 1:1 but the nearest Windows step is not the phone's exact DPR.</summary>
    Quantized,
    /// <summary>
    /// Windows caps the scale below what the phone needs. Pixels were reduced so the LOGICAL
    /// canvas stays phone-sized; the phone must upscale, so text is softer than native.
    /// </summary>
    CappedReducedPixels,
    /// <summary>
    /// Reducing pixels did not help (the cap is a floor on the logical desktop size). Pixels
    /// stay 1:1 at the highest allowed scale, so the layout is WIDER than the phone's.
    /// </summary>
    CappedWiderLayout,
    /// <summary>A larger backing desktop was measured at exact DPI; the phone sees
    /// a 1:1 pixel viewport with its native logical and physical dimensions.</summary>
    VerifiedViewport,
    /// <summary>
    /// A larger backing desktop was measured at the nearest Windows scale step.
    /// The phone still receives a 1:1 physical-pixel viewport, but the app layout
    /// is quantized to that step rather than the phone's exact DPR.
    /// </summary>
    QuantizedViewport,
}
public sealed record DensityPlan(
    PlanKind Kind,
    int PixelW,
    int PixelH,
    int ScalePercent,
    int WantedPercent,
    bool PixelsOneToOne,
    double CanvasDpW,
    double CanvasDpH,
    double ApparentTokenDp,
    string? Reason)
{
    public int PlannedDpi => WindowsScale.DpiFor(ScalePercent);
    /// <summary>True only if BOTH the pixel mapping and the density are exactly the phone's.</summary>
    public bool WouldBeNative => (Kind == PlanKind.Exact || Kind == PlanKind.VerifiedViewport) && PixelsOneToOne;
}
public static class DensityPolicy
{
    public const double ReferenceTokenDp = 14.0;
    public const double ReadableTokenDp = 12.0;
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static int Even(double v)
    {
        var r = (int)Math.Round(v);
        return Math.Max(2, r - (r % 2));
    }
    public static double TokenDp(int scalePercent, double dpr, double upscale = 1.0) =>
        ReferenceTokenDp * (scalePercent / 100.0) * upscale / dpr;
    /// <summary>First plan: full pixels at the nearest step, or reduced pixels if Windows' cap forbids it.</summary>
    public static DensityPlan Plan(PhoneProfile p, ScaleCaps? caps)
    {
        var wantPercent = p.Dpr * 100.0;
        var step = WindowsScale.NearestStep(wantPercent);
        var canvasW = p.PhysicalW / (double)p.Dpr;
        var canvasH = p.PhysicalH / (double)p.Dpr;
        if (caps is not null && step > caps.MaxPercent)
        {
            var capped = WindowsScale.LargestStepAtMost(caps.MaxPercent);
            var pw = Even(canvasW * capped / 100.0);
            var ph = Even(canvasH * capped / 100.0);
            var upscale = p.PhysicalW / (double)pw;
            return new DensityPlan(
                PlanKind.CappedReducedPixels, pw, ph, capped, (int)Math.Round(wantPercent), false,
                pw / (capped / 100.0), ph / (capped / 100.0), TokenDp(capped, p.Dpr, upscale),
                $"Windows caps this monitor at {caps.MaxPercent}% but the phone needs {step}%. " +
                $"Rendering {pw}x{ph} px at {capped}% keeps the {F(canvasW)}x{F(canvasH)} dp layout, but with only " +
                $"{F(100.0 * pw / p.PhysicalW)}% of the phone's pixels, so the phone has to upscale it and text is softer than native.");
        }
        var exact = Math.Abs(step - wantPercent) <= 1.0;
        var token = TokenDp(step, p.Dpr);
        if (exact)
        {
            return new DensityPlan(PlanKind.Exact, p.PhysicalW, p.PhysicalH, step, (int)Math.Round(wantPercent), true,
                p.PhysicalW / (step / 100.0), p.PhysicalH / (step / 100.0), token, null);
        }
        var lower = WindowsScale.LargestStepAtMost((int)Math.Floor(wantPercent));
        var upper = WindowsScale.SmallestStepAtLeast((int)Math.Ceiling(wantPercent));
        var drift = (token / ReferenceTokenDp - 1.0) * 100.0;
        return new DensityPlan(PlanKind.Quantized, p.PhysicalW, p.PhysicalH, step, (int)Math.Round(wantPercent), true,
            p.PhysicalW / (step / 100.0), p.PhysicalH / (step / 100.0), token,
            $"Windows only offers {lower}% and {upper}%, not {F(wantPercent)}%. Using {step}% ({WindowsScale.DpiFor(step)} DPI) " +
            $"instead of {p.ExpectedDpi} DPI: the layout is {F(Math.Abs(drift))}% {(drift < 0 ? "smaller" : "larger")} than the phone's exact density " +
            "(a 14 dp reference token measures " + F(token) + " dp). Pixels remain 1:1 and sharp.");
    }
    /// <summary>
    /// Plan B when shrinking pixels did not raise the cap: keep every pixel and use the
    /// highest scale Windows allows. The layout is wider than the phone's logical canvas and
    /// small UI text will be small; the numbers are reported, not hidden.
    /// </summary>
    public static DensityPlan FallbackWiderLayout(PhoneProfile p, ScaleCaps caps)
    {
        var capped = WindowsScale.LargestStepAtMost(caps.MaxPercent);
        var token = TokenDp(capped, p.Dpr);
        return new DensityPlan(
            PlanKind.CappedWiderLayout, p.PhysicalW, p.PhysicalH, capped, (int)Math.Round(p.Dpr * 100.0), true,
            p.PhysicalW / (capped / 100.0), p.PhysicalH / (capped / 100.0), token,
            $"Windows currently reports a {caps.MaxPercent}% maximum for the virtual monitor; its behavior at other modes is not established. " +
            "The requested native-resolution monitor is preserved. A " +
            $"{F(p.PhysicalW / p.Dpr)} dp-wide phone layout is not achievable. Using {capped}% at full pixels: apps lay out " +
            $"{F(p.PhysicalW / (capped / 100.0))} dp wide and a 14 dp reference token measures {F(token)} dp on the phone" +
            (token < ReadableTokenDp ? " — below the readability floor; use the app's own zoom." : "."));
    }
}
