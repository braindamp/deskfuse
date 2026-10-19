using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DprChallengeTheoryTests
{
    [Theory]
    [InlineData(1200, 175)]
    [InlineData(1350, 200)]
    [InlineData(1440, 225)]
    [InlineData(1500, 225)]
    [InlineData(1600, 250)]
    [InlineData(1800, 250)]
    [InlineData(1920, 300)]
    [InlineData(2100, 300)]
    [InlineData(2240, 350)]
    [InlineData(2560, 400)]
    public void BB_640_DIP_prediction_is_not_mistaken_for_hardware_evidence(int width, int expected)
    {
        Assert.Equal(expected, DprChallengeTheory.PredictedMaximum(width, 3120, 640));
    }

    // These are actual maximum scaling readbacks from the Windows 10 FocusIdd
    // challenge runs (October 19, 2026). They support, but do not prove, the
    // 800x600 effective-desktop constraint inferred from mode geometry.
    [Theory]
    [InlineData(1440, 3120, 225)]
    [InlineData(1440, 3900, 225)]
    [InlineData(1600, 3120, 250)]
    [InlineData(1920, 3120, 300)]
    [InlineData(2100, 3120, 350)]
    [InlineData(3120, 1440, 225)]
    [InlineData(2560, 1080, 175)]
    [InlineData(1200, 1200, 150)]
    public void Orientation_independent_800x600_hypothesis_matches_measured_scaling_caps(
        int width, int height, int observedMax)
    {
        Assert.Equal(observedMax,
            DprChallengeTheory.PredictedMaximumForLogicalDesktop(width, height));
    }

    [Fact]
    public void Square_display_requires_800_dips_in_both_directions()
    {
        // A 600x600 effective desktop is NOT enough despite the short-side
        // 600 DIP floor. With 1200x1200 source, 150% is the largest model step.
        Assert.Equal(150, DprChallengeTheory.PredictedMaximumForLogicalDesktop(1200, 1200));
        Assert.Equal(200, DprChallengeTheory.PredictedMaximumForLogicalDesktop(1600, 1600));
    }

    [Fact]
    public void A_350_percent_mode_is_allowed_by_geometry_only_at_a_short_side_of_at_least_2100_px()
    {
        Assert.Equal(300, DprChallengeTheory.PredictedMaximumForLogicalDesktop(2098, 3120));
        Assert.Equal(350, DprChallengeTheory.PredictedMaximumForLogicalDesktop(2100, 3120));
        Assert.Equal(350, DprChallengeTheory.PredictedMaximumForLogicalDesktop(2102, 3120));
        Assert.Equal(225, DprChallengeTheory.PredictedMaximumForLogicalDesktop(1440, 3120));
    }

    // Exact two-axis boundaries proposed by Arena.ai and refined to the
    // adjacent even-pixel modes. To be validated against Windows -3 readback.
    [Theory]
    [InlineData(2100, 2800, 350)] // 600x800 DIPs at 350%
    [InlineData(2100, 2798, 300)] // long edge <800 DIPs at 350%
    [InlineData(1200, 1600, 200)] // 600x800 DIPs at 200%
    [InlineData(1200, 1598, 175)] // long edge <800 DIPs at 200%
    [InlineData(1198, 1600, 175)] // short edge <600 DIPs at 200%
    public void Both_logical_axes_constrain_the_model(
        int width, int height, int predictedMax)
    {
        Assert.Equal(predictedMax,
            DprChallengeTheory.PredictedMaximumForLogicalDesktop(width, height));
    }

    [Theory]
    [InlineData(200, 350, 4)]
    [InlineData(100, 350, 8)]
    [InlineData(350, 225, -3)]
    public void Scale_offsets_are_relative_step_indices_not_percentage_differences(
        int recommended, int desired, int expected)
    {
        Assert.Equal(expected, DprChallengeTheory.UnrestrictedRelativeStep(recommended, desired));
    }

    [Fact]
    public void Out_of_range_scale_still_requires_an_explicit_unsafe_opt_in()
    {
        var caps = new ScaleCaps(200, 200, 225);
        Assert.False(caps.Allows(350));
        Assert.Throws<ArgumentOutOfRangeException>(() => caps.RelativeFor(350));
        Assert.Equal(4, DprChallengeTheory.UnrestrictedRelativeStep(caps.RecommendedPercent, 350));
    }

    [Fact]
    public void Cropped_pixels_are_identical_and_not_resampled()
    {
        const int sourceW = 5;
        const int sourceH = 4;
        var frame = new byte[sourceW * sourceH * 4];
        for (var i = 0; i < sourceW * sourceH; i++)
        {
            frame[i * 4] = (byte)(i + 1);
            frame[i * 4 + 3] = 255;
        }
        var visible = new RectI(1, 1, 3, 2);
        var pixels = DprChallengeTheory.CopyViewportBgra(frame, sourceW, sourceH, visible);
        Assert.Equal(3 * 2 * 4, pixels.Length);
        Assert.Equal(new byte[] { 7, 8, 9, 12, 13, 14 },
            Enumerable.Range(0, 6).Select(i => pixels[i * 4]).ToArray());
    }

    [Fact]
    public void Crop_refuses_out_of_bounds_rectangles()
    {
        var frame = new byte[5 * 4 * 4];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DprChallengeTheory.CopyViewportBgra(frame, 5, 4, new RectI(2, 0, 4, 3)));
        Assert.Throws<ArgumentException>(() =>
            DprChallengeTheory.CopyViewportBgra(new byte[3], 5, 4, new RectI(0, 0, 3, 2)));
    }
}
