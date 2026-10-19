using FocusWindow.Core.Protocol;
namespace FocusWindow.Core.Display;
public sealed record RulerResult(int ExpectedPx, int MeasuredWidthPx, int MeasuredHeightPx, bool Ok, string Detail);
/// <summary>
/// Measures what actually ended up in a captured frame. A reported DPI is only a number
/// Windows handed us; this measures PIXELS: a square the probe window drew at 100 DIPs, found
/// again in the BGRA frame that came back through the driver. It proves the capture path does
/// not resample, and — combined with the DPI the window reported — that the window was
/// rendered at that density.
/// </summary>
public static class DensityMeasure
{
    /// <summary>
    /// Finds the bounding box of all pixels within <paramref name="tolerance"/> (per channel)
    /// of the given colour. Returns false if fewer than <paramref name="minPixels"/> match.
    /// </summary>
    public static bool TryFindColorBox(
        ReadOnlySpan<byte> bgra, int width, int height,
        byte b, byte g, byte r, int tolerance, out RectI box, int minPixels = 16)
    {
        box = default;
        if (bgra.Length < (long)width * height * 4) return false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var o = x * 4;
                if (Math.Abs(row[o] - b) <= tolerance &&
                    Math.Abs(row[o + 1] - g) <= tolerance &&
                    Math.Abs(row[o + 2] - r) <= tolerance)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    count++;
                }
            }
        }
        if (count < minPixels) return false;
        box = new RectI(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }
    /// <summary>
    /// Compares a measured square against <c>squareDips * dpi / 96</c>. The tolerance is
    /// max(2 px, 1.5%) per side, enough for anti-aliasing at the edges and not enough to hide a
    /// real 5% scale error (a 262.5% request landing on 250% is a 4.8% miss and WOULD fail).
    /// </summary>
    public static RulerResult EvaluateRuler(RectI measured, int squareDips, int reportedDpi)
    {
        var expected = (int)Math.Round(squareDips * reportedDpi / 96.0);
        var tolerance = Math.Max(2, (int)Math.Ceiling(expected * 0.015));
        var okW = Math.Abs(measured.Width - expected) <= tolerance;
        var okH = Math.Abs(measured.Height - expected) <= tolerance;
        var ok = okW && okH;
        var detail = ok
            ? $"{squareDips} DIP square measured {measured.Width}x{measured.Height} px; expected {expected} px at {reportedDpi} DPI (±{tolerance})"
            : $"{squareDips} DIP square measured {measured.Width}x{measured.Height} px but {reportedDpi} DPI implies {expected} px (±{tolerance}): " +
              "the window was NOT rendered at the DPI it reported, or the capture path resampled it";
        return new RulerResult(expected, measured.Width, measured.Height, ok, detail);
    }
}
