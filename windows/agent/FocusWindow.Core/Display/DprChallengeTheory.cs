using FocusWindow.Core.Protocol;

namespace FocusWindow.Core.Display;

/// <summary>
/// Pure, driver-free calculations for the FA/BB 350%-DPI challenge.
/// A predicted scale limit is NEVER evidence that Windows applied that scale.
/// </summary>
public static class DprChallengeTheory
{
    public static int PredictedMaximum(int width, int height, int minimumLogicalWidth,
        int minimumLogicalHeight = 480)
    {
        if (width <= 0 || height <= 0 || minimumLogicalWidth <= 0 || minimumLogicalHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive.");
        var maximum = 0;
        foreach (var percent in WindowsScale.Steps)
        {
            if (width * 100.0 / percent >= minimumLogicalWidth &&
                height * 100.0 / percent >= minimumLogicalHeight)
                maximum = percent;
        }
        return maximum;
    }

    /// <summary>
    /// Experimental -4 packet's step offset from the recommended step.
    /// Unlike ScaleCaps.RelativeFor this intentionally does NOT check the reported cap.
    /// Never call an OS setter merely because this calculation succeeded.
    /// </summary>
    public static int UnrestrictedRelativeStep(int recommendedPercent, int desiredPercent)
    {
        var recommended = WindowsScale.IndexOf(recommendedPercent);
        var desired = WindowsScale.IndexOf(desiredPercent);
        if (recommended < 0 || desired < 0)
            throw new ArgumentOutOfRangeException(nameof(desiredPercent), "Unknown Windows scale step.");
        return desired - recommended;
    }

    /// <summary>
    /// Exact BGRA row copy from a driver frame. This is a TEST-ONLY CPU crop, not a
    /// production GPU crop or a claim that NVENC has been wired to the viewport.
    /// Every output pixel equals one source pixel; no interpolation or resizing.
    /// </summary>
    public static byte[] CopyViewportBgra(ReadOnlySpan<byte> fullBgra, int sourceWidth,
        int sourceHeight, RectI viewport)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            viewport.X < 0 || viewport.Y < 0 || viewport.Width <= 0 || viewport.Height <= 0 ||
            (long)viewport.X + viewport.Width > sourceWidth ||
            (long)viewport.Y + viewport.Height > sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(viewport), "Crop must be within the frame.");
        var sourceBytes = checked((long)sourceWidth * sourceHeight * 4);
        var destinationBytes = checked((long)viewport.Width * viewport.Height * 4);
        if (fullBgra.Length < sourceBytes || destinationBytes > int.MaxValue)
            throw new ArgumentException("Invalid BGRA source length or crop dimensions.");
        var result = new byte[(int)destinationBytes];
        var rowLength = checked(viewport.Width * 4);
        for (var y = 0; y < viewport.Height; y++)
        {
            var offset = checked(((viewport.Y + y) * sourceWidth + viewport.X) * 4);
            fullBgra.Slice(offset, rowLength).CopyTo(result.AsSpan(y * rowLength, rowLength));
        }
        return result;
    }
}
