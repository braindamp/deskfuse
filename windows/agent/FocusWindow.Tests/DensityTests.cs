using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
using Xunit;
namespace FocusWindow.Tests;
public class DensityPolicyTests
{
    [Fact]
    public void Flagship_3_5_is_exact_at_350_percent_336_dpi()
    {
        var plan = DensityPolicy.Plan(TestProfiles.Flagship(), new ScaleCaps(250, 250, 500));
        Assert.Equal(PlanKind.Exact, plan.Kind);
        Assert.Equal(350, plan.ScalePercent);
        Assert.Equal(336, plan.PlannedDpi);
        Assert.Equal(1440, plan.PixelW);
        Assert.Equal(3120, plan.PixelH);
        Assert.True(plan.PixelsOneToOne);
        Assert.True(plan.WouldBeNative);
        Assert.Equal(411.4, plan.CanvasDpW, 1);   // 1440 / 3.5
        Assert.Equal(891.4, plan.CanvasDpH, 1);   // 3120 / 3.5
        Assert.Equal(14.0, plan.ApparentTokenDp, 1);
        Assert.Null(plan.Reason);
    }
    [Theory]
    [InlineData(2.0f, 200, 192)]
    [InlineData(2.25f, 225, 216)]
    [InlineData(2.5f, 250, 240)]
    [InlineData(3.0f, 300, 288)]
    [InlineData(3.5f, 350, 336)]
    [InlineData(4.0f, 400, 384)]
    public void Common_android_densities_that_windows_can_match_are_exact(float dpr, int percent, int dpi)
    {
        var plan = DensityPolicy.Plan(TestProfiles.Flagship(dpr), new ScaleCaps(100, 100, 500));
        Assert.Equal(PlanKind.Exact, plan.Kind);
        Assert.Equal(percent, plan.ScalePercent);
        Assert.Equal(dpi, plan.PlannedDpi);
    }
    [Fact]
    public void Dpr_2_625_cannot_be_matched_and_the_plan_says_so_instead_of_pretending()
    {
        // 2.625 is the density of a very common 420 dpi Android bucket. Windows has no 262.5%.
        var p = TestProfiles.Flagship(2.625f, 1080, 2400, 150);
        var plan = DensityPolicy.Plan(p, new ScaleCaps(100, 100, 500));
        Assert.Equal(PlanKind.Quantized, plan.Kind);
        Assert.Equal(250, plan.ScalePercent);
        Assert.Equal(240, plan.PlannedDpi);
        Assert.Equal(252, p.ExpectedDpi);
        Assert.True(plan.PixelsOneToOne);            // pixels are still 1:1 and sharp
        Assert.False(plan.WouldBeNative);            // but it is NOT the phone's exact density
        Assert.Contains("250%", plan.Reason);
        Assert.Contains("300%", plan.Reason);
        Assert.Equal(13.3, plan.ApparentTokenDp, 1); // a 14 dp token lands at 13.3 dp
    }
    [Fact]
    public void Dpr_2_75_snaps_to_300()
    {
        var plan = DensityPolicy.Plan(TestProfiles.Flagship(2.75f, 1080, 2400, 150), new ScaleCaps(100, 100, 500));
        Assert.Equal(300, plan.ScalePercent);
        Assert.Equal(PlanKind.Quantized, plan.Kind);
    }
    [Fact]
    public void Nearest_step_is_log_symmetric_and_ties_go_up()
    {
        Assert.Equal(250, WindowsScale.NearestStep(273)); // geometric mean of 250 and 300 is 273.86
        Assert.Equal(300, WindowsScale.NearestStep(274));
        Assert.Equal(100, WindowsScale.NearestStep(50));
        Assert.Equal(500, WindowsScale.NearestStep(900));
    }
    [Fact]
    public void A_scale_cap_below_the_need_reduces_pixels_so_the_phone_sized_layout_survives()
    {
        var plan = DensityPolicy.Plan(TestProfiles.Flagship(), new ScaleCaps(250, 250, 250));
        Assert.Equal(PlanKind.CappedReducedPixels, plan.Kind);
        Assert.Equal(250, plan.ScalePercent);
        Assert.Equal(1028, plan.PixelW);             // 411.4 dp * 2.5
        Assert.Equal(2228, plan.PixelH);
        Assert.False(plan.PixelsOneToOne);
        Assert.Equal(14.0, plan.ApparentTokenDp, 0); // layout-correct once the phone upscales
        Assert.Contains("softer", plan.Reason);
        Assert.Equal(0, plan.PixelW % 2);            // H.264 4:2:0 needs even sizes
        Assert.Equal(0, plan.PixelH % 2);
    }
    [Fact]
    public void When_shrinking_pixels_cannot_help_the_fallback_reports_a_wider_layout_and_unreadable_tokens()
    {
        var plan = DensityPolicy.FallbackWiderLayout(TestProfiles.Flagship(), new ScaleCaps(100, 100, 175));
        Assert.Equal(PlanKind.CappedWiderLayout, plan.Kind);
        Assert.Equal(175, plan.ScalePercent);
        Assert.Equal(1440, plan.PixelW);
        Assert.True(plan.PixelsOneToOne);
        Assert.Equal(7.0, plan.ApparentTokenDp, 1);  // 14 * 1.75 / 3.5
        Assert.True(plan.ApparentTokenDp < DensityPolicy.ReadableTokenDp);
        Assert.Contains("below the readability floor", plan.Reason);
    }
    [Fact]
    public void Scale_caps_decode_the_relative_packet_and_reject_nonsense()
    {
        // minRel = -6 means "recommended is index 6 (250%)"; cur 0 = recommended; max +5 = index 11 (500%).
        var caps = ScaleCaps.FromRelative(-6, 0, 5);
        Assert.Equal(new ScaleCaps(250, 250, 500), caps);
        Assert.Equal(new ScaleCaps(100, 125, 200), ScaleCaps.FromRelative(0, 1, 4));
        Assert.Null(ScaleCaps.FromRelative(-20, 0, 0));   // outside the table: Windows changed the packet
        Assert.Null(ScaleCaps.FromRelative(-6, -9, 0));
        Assert.Null(ScaleCaps.FromRelative(-6, 0, 40));
    }
    [Fact]
    public void Relative_index_for_a_target_is_measured_from_the_recommended_step()
    {
        var caps = new ScaleCaps(250, 250, 500);
        Assert.Equal(2, caps.RelativeFor(350));    // index 8 - index 6
        Assert.Equal(0, caps.RelativeFor(250));
        Assert.Equal(-3, caps.RelativeFor(175));   // index 3 - index 6
        Assert.Throws<ArgumentOutOfRangeException>(() => caps.RelativeFor(262));  // not a step
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleCaps(250, 250, 300).RelativeFor(350)); // above the cap
    }
}
public class DensityMeasureTests
{
    [Fact]
    public void Finds_the_square_exactly_and_the_ruler_passes_when_pixels_match_the_reported_dpi()
    {
        // 100 DIP at 336 DPI = 350 px.
        var frame = TestFrames.Make(1440, 1000, 255, 0, 255, squareX: 100, squareY: 100, square: 350);
        Assert.True(DensityMeasure.TryFindColorBox(frame, 1440, 1000, 255, 255, 255, 10, out var box));
        Assert.Equal(new RectI(100, 100, 350, 350), box);
        var result = DensityMeasure.EvaluateRuler(box, 100, 336);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(350, result.ExpectedPx);
    }
    [Fact]
    public void A_window_rendered_at_240_dpi_while_reporting_336_fails_the_ruler()
    {
        // This is the exact lie the probe exists to catch: a number says 336, pixels say 240.
        var frame = TestFrames.Make(1440, 1000, 255, 0, 255, squareX: 100, squareY: 100, square: 250);
        Assert.True(DensityMeasure.TryFindColorBox(frame, 1440, 1000, 255, 255, 255, 10, out var box));
        var result = DensityMeasure.EvaluateRuler(box, 100, 336);
        Assert.False(result.Ok);
        Assert.Contains("NOT rendered at the DPI it reported", result.Detail);
    }
    [Fact]
    public void Returns_false_when_the_square_is_absent()
    {
        var frame = TestFrames.Make(200, 200, 255, 0, 255, squareX: 500, squareY: 500);
        Assert.False(DensityMeasure.TryFindColorBox(frame, 200, 200, 255, 255, 255, 10, out _));
    }
}
public class DensityNegotiatorTests
{
    private static DensityNegotiator Negotiator(FakeDisplayBackend be, bool ruler = true, bool experimental = true) =>
        new(be, new NegotiatorOptions { DrawRuler = ruler, SettleTimeoutMs = 300,
            AllowExperimentalScaleWrite = experimental }, new ListLog());
    private static async Task<NegotiationOutcome> Run(FakeDisplayBackend be, PhoneProfile? p = null,
        bool ruler = true, bool experimental = true) =>
        await Negotiator(be, ruler, experimental).RunAsync(p ?? TestProfiles.Flagship(), null, CancellationToken.None);
    [Fact]
    public async Task Production_defaults_never_write_undocumented_display_scale()
    {
        await using var be = new FakeDisplayBackend { RecommendedPercent = 250 };
        var o = await new DensityNegotiator(be, new NegotiatorOptions { DrawRuler = true, SettleTimeoutMs = 300 },
            new ListLog()).RunAsync(TestProfiles.Flagship(), null, CancellationToken.None);
        Assert.Empty(be.SetCalls);
        Assert.False(o.Native);
        Assert.Equal(EvidenceStatus.Warn, o.Evidence.Find("scale-applied")!.Status);
        Assert.Equal(240, o.EffectiveDpi);
    }
    [Fact]
    public async Task Wrong_client_area_never_claims_native_density()
    {
        await using var be = new FakeDisplayBackend { ProbeClientFullSize = false };
        var o = await Run(be, ruler: true);
        Assert.False(o.Native);
        Assert.Equal(EvidenceStatus.Fail, o.Evidence.Find("probe-client-size")!.Status);
    }
    [Fact]
    public async Task Missing_probe_window_never_claims_native_density()
    {
        await using var be = new FakeDisplayBackend { ProbeAvailable = false };
        var o = await Run(be, ruler: true);
        Assert.False(o.Native);
        Assert.Equal(EvidenceStatus.Fail, o.Evidence.Find("window-dpi")!.Status);
    }
    [Fact]
    public async Task Windows_already_recommends_the_right_scale_so_nothing_is_overridden_and_the_session_is_native()
    {
        await using var be = new FakeDisplayBackend { RecommendedPercent = 350 };
        var o = await Run(be);
        Assert.True(o.Native);
        Assert.Equal(DensityMethod.AlreadyCorrect, o.Method);
        Assert.Empty(be.SetCalls);
        Assert.Equal(336, o.EffectiveDpi);
        Assert.Null(o.DegradedReason);
        Assert.Equal(EvidenceStatus.Pass, o.Evidence.Find("monitor-dpi")!.Status);
        Assert.Equal(EvidenceStatus.Pass, o.Evidence.Find("window-dpi")!.Status);
        Assert.False(o.Evidence.HasFailure);
    }
    [Fact]
    public async Task The_edid_physical_size_and_hint_are_derived_from_the_phone_profile()
    {
        await using var be = new FakeDisplayBackend();
        await Run(be);
        var req = be.Created[0];
        Assert.Equal(1440, req.Width);
        Assert.Equal(3120, req.Height);
        Assert.Equal(120, req.RefreshHz);
        Assert.InRange(req.PhysWidthMm, 70, 72);     // 170 mm diagonal at 1440:3120
        Assert.InRange(req.PhysHeightMm, 153, 155);
        Assert.Equal(350, req.ScaleHintPercent);
        Assert.NotEqual(0u, req.Serial);
    }
    [Fact]
    public async Task When_windows_recommends_less_the_agent_overrides_it_and_reads_the_result_back()
    {
        await using var be = new FakeDisplayBackend { RecommendedPercent = 250 };
        var o = await Run(be);
        Assert.Equal(new[] { 350 }, be.SetCalls);
        Assert.Equal(DensityMethod.PerMonitorOverride, o.Method);
        Assert.True(o.Native);
        Assert.Equal(336, o.EffectiveDpi);
        Assert.Equal(EvidenceStatus.Pass, o.Evidence.Find("scale-applied")!.Status);
    }
    [Fact]
    public async Task A_refused_override_is_reported_as_a_failure_not_hidden()
    {
        await using var be = new FakeDisplayBackend { RecommendedPercent = 250, AcceptSet = false };
        var o = await Run(be);
        Assert.False(o.Native);
        Assert.Equal(DensityMethod.FallbackScaled, o.Method);
        Assert.Equal(240, o.EffectiveDpi);           // the truth: Windows is still at 250%
        Assert.Equal(EvidenceStatus.Fail, o.Evidence.Find("scale-applied")!.Status);
        Assert.Equal(EvidenceStatus.Fail, o.Evidence.Find("monitor-dpi")!.Status);
        Assert.Contains("240", o.DegradedReason);
        Assert.Contains("336", o.DegradedReason);
    }
    [Fact]
    public async Task An_override_that_is_accepted_but_not_actually_applied_is_caught_by_the_read_back()
    {
        // The dangerous case: the API returns success, Windows keeps rendering at 240 DPI.
        await using var be = new FakeDisplayBackend { RecommendedPercent = 250, DpiAppliedForSetPercent = _ => 240 };
        var o = await Run(be);
        Assert.Equal(EvidenceStatus.Pass, o.Evidence.Find("scale-applied")!.Status); // call "succeeded"
        Assert.Equal(EvidenceStatus.Fail, o.Evidence.Find("monitor-dpi")!.Status);   // ...and the truth disagrees
        Assert.False(o.Native);
        Assert.Equal(240, o.EffectiveDpi);
    }
    [Fact]
    public async Task A_dpi_unaware_window_on_the_monitor_makes_the_session_non_native()
    {
        await using var be = new FakeDisplayBackend { ProbeWindowDpi = 96, ProbeAwareness = "Unaware (DPI-virtualised)" };
        var o = await Run(be);
        Assert.False(o.Native);
        Assert.Equal(EvidenceStatus.Fail, o.Evidence.Find("window-dpi")!.Status);
        Assert.Contains("Unaware", o.DegradedReason);
    }
    [Fact]
    public async Task A_failing_pixel_ruler_makes_the_session_non_native_even_if_every_number_agrees()
    {
        await using var be = new FakeDisplayBackend { ProbeRulerOk = false };
        var o = await Run(be, ruler: true);
        Assert.False(o.Native);
        Assert.Equal(EvidenceStatus.Fail, o.Evidence.Find("ruler")!.Status);
    }
    [Fact]
    public async Task A_passing_pixel_ruler_is_recorded_as_evidence()
    {
        await using var be = new FakeDisplayBackend();
        var o = await Run(be, ruler: true);
        Assert.True(o.Native);
        Assert.Equal(EvidenceStatus.Pass, o.Evidence.Find("ruler")!.Status);
    }
    [Fact]
    public async Task Dpr_2_625_is_streamed_crisply_but_never_labelled_native()
    {
        await using var be = new FakeDisplayBackend { RecommendedPercent = 250 };
        var o = await Run(be, TestProfiles.Flagship(2.625f, 1080, 2400, 150));
        Assert.Equal(PlanKind.Quantized, o.Plan.Kind);
        Assert.Equal(1080, o.Plan.PixelW);           // pixels stay 1:1
        Assert.Equal(240, o.EffectiveDpi);
        Assert.False(o.Native);
        Assert.Contains("only offers 250% and 300%", o.DegradedReason);
    }
    [Fact]
    public async Task Dpi_proof_preserves_native_pixels_even_when_Windows_reports_a_scale_cap()
    {
        await using var be = new FakeDisplayBackend
        {
            RecommendedPercent = 225,
            MaxPercentFor = (_, _) => 225,
        };
        var outcome = await new DensityNegotiator(be,
            new NegotiatorOptions
            {
                PreserveRequestedResolution = true,
                DrawRuler = true,
                SettleTimeoutMs = 300,
                AllowExperimentalScaleWrite = false,
            }, new ListLog()).RunAsync(TestProfiles.Flagship(), null, CancellationToken.None);
        // An active 1440x3120 monitor must never be replaced by a 926x2006 one
        // just because Windows reports that 350% is unavailable.
        Assert.Single(be.Created);
        Assert.Equal((1440, 3120), (be.Created[0].Width, be.Created[0].Height));
        Assert.Equal(PlanKind.CappedWiderLayout, outcome.Plan.Kind);
        Assert.Equal((1440, 3120), (outcome.Plan.PixelW, outcome.Plan.PixelH));
        Assert.Equal(EvidenceStatus.Warn, outcome.Evidence.Find("cap-vs-pixels")!.Status);
        Assert.Empty(be.SetCalls);
        Assert.False(outcome.Native);
        Assert.Contains("no resolution fallback", outcome.DegradedReason!.ToLowerInvariant());
    }

    [Fact]
    public async Task A_cap_that_pixel_reduction_can_beat_recreates_the_monitor_smaller()
    {
        await using var be = new FakeDisplayBackend
        {
            RecommendedPercent = 250,
            MaxPercentFor = (w, _) => w >= 1440 ? 250 : 300,
        };
        var o = await Run(be);
        Assert.Equal(PlanKind.CappedReducedPixels, o.Plan.Kind);
        Assert.Equal(2, be.Created.Count);
        Assert.Equal((1028, 2228), (be.Created[1].Width, be.Created[1].Height));
        Assert.Equal(250, o.Plan.ScalePercent);
        Assert.False(o.Native);                      // upscaled on the phone => not native
        Assert.Contains("softer", o.DegradedReason);
    }
    [Fact]
    public async Task A_cap_that_is_a_logical_size_floor_falls_back_to_full_pixels_and_a_wider_layout()
    {
        // The cap falls with the pixel count (cap = pixels / 5.76): the signature of a floor on
        // the LOGICAL desktop size. Shrinking pixels cannot reach a phone-sized layout.
        await using var be = new FakeDisplayBackend
        {
            RecommendedPercent = 250,
            MaxPercentFor = (w, _) => (int)(w / 5.76),
        };
        var o = await Run(be);
        Assert.Equal(3, be.Created.Count);           // full, reduced (tried), full again
        Assert.Equal((1440, 3120), (be.Created[2].Width, be.Created[2].Height));
        Assert.Equal(PlanKind.CappedWiderLayout, o.Plan.Kind);
        Assert.Equal(250, o.Plan.ScalePercent);
        Assert.Equal(1440, o.Plan.PixelW);
        Assert.False(o.Native);
        Assert.Equal(EvidenceStatus.Warn, o.Evidence.Find("cap-vs-pixels")!.Status);
        Assert.Contains("readability floor", o.DegradedReason);
    }
    [Fact]
    public async Task When_the_undocumented_caps_query_fails_the_agent_does_not_guess_and_reports_what_windows_chose()
    {
        await using var be = new FakeDisplayBackend { RecommendedPercent = 250, CapsAvailable = false };
        var o = await Run(be);
        Assert.Empty(be.SetCalls);                   // cannot set what it cannot read
        Assert.Equal(EvidenceStatus.Warn, o.Evidence.Find("windows-scale-caps")!.Status);
        Assert.Equal(240, o.EffectiveDpi);
        Assert.False(o.Native);
        Assert.Equal(DensityMethod.FallbackScaled, o.Method);
    }
    [Fact]
    public async Task The_iddcx_scale_api_limitation_is_part_of_every_evidence_log()
    {
        await using var be = new FakeDisplayBackend();
        var o = await Run(be);
        var item = o.Evidence.Find("iddcx-scale-api")!;
        Assert.Equal(EvidenceStatus.Info, item.Status);
        Assert.Contains("remote-session", item.Note);
    }
    [Fact]
    public async Task An_unusable_profile_is_a_named_failure_with_a_remediation()
    {
        await using var be = new FakeDisplayBackend();
        var bad = TestProfiles.Flagship() with { Dpr = 9f };
        var ex = await Assert.ThrowsAsync<DisplayException>(() => Run(be, bad));
        Assert.Equal("profile", ex.Stage);
        Assert.NotNull(ex.Remediation);
        var odd = TestProfiles.Flagship(3.5f, 1439, 3120);
        var ex2 = await Assert.ThrowsAsync<DisplayException>(() => Run(be, odd));
        Assert.Contains("even", ex2.Message);
    }
    [Fact]
    public async Task Evidence_renders_to_markdown_and_json_with_every_check()
    {
        await using var be = new FakeDisplayBackend();
        var o = await Run(be, ruler: true);
        var md = o.Evidence.ToMarkdown("test");
        Assert.Contains("| PASS |", md);
        Assert.Contains("Monitor effective DPI", md);
        Assert.Contains("\"monitor-dpi\"", o.Evidence.ToJsonString());
    }
}
