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
    /// Exact 1:1 BGRA crop with caller-owned reusable output memory.
    /// No interpolation, scale filter or stream size change.
    /// The driver still renders the full virtual surface; copying only the
    /// phone viewport avoids encoding the hidden pixels.
    /// </summary>
    public static void CopyViewportBgra(ReadOnlySpan<byte> fullBgra,
        int sourceWidth, int sourceHeight, RectI viewport, Span<byte> destination)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            viewport.X < 0 || viewport.Y < 0 || viewport.Width <= 0 || viewport.Height <= 0 ||
            (long)viewport.X + viewport.Width > sourceWidth ||
            (long)viewport.Y + viewport.Height > sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(viewport),
                "Viewport must fit inside the backing display.");
        var sourceBytes = checked((long)sourceWidth * sourceHeight * 4);
        var targetBytes = checked((long)viewport.Width * viewport.Height * 4);
        if (fullBgra.Length < sourceBytes || targetBytes > int.MaxValue ||
            destination.Length < targetBytes)
            throw new ArgumentException("Insufficient BGRA source/destination length.");
        var rowLength = checked(viewport.Width * 4);
        for (var y = 0; y < viewport.Height; y++)
        {
            var inputRow = checked(((viewport.Y + y) * sourceWidth + viewport.X) * 4);
            fullBgra.Slice(inputRow, rowLength).CopyTo(destination.Slice(y * rowLength, rowLength));
        }
    }

    /// <summary>Allocation convenience for evidence capture; streaming reuses a buffer.</summary>
    public static byte[] CopyViewportBgra(ReadOnlySpan<byte> fullBgra, int sourceWidth,
        int sourceHeight, RectI viewport)
    {
        var result = new byte[checked(viewport.Width * viewport.Height * 4)];
        CopyViewportBgra(fullBgra, sourceWidth, sourceHeight, viewport, result);
        return result;
    }
}
