using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
using Xunit;

namespace FocusWindow.Tests;

public sealed class AdaptiveViewportTests
{
    private static AdaptiveViewportCalibration NewCalibration(FakeDisplayBackend backend) =>
        new(backend, new StaticGuard(), new StaticGuard().Snapshot(), new ListLog())
        {
            InitialSettlingMs = 0,
            RetrySettlingMs = 0
        };

    [Theory]
    [InlineData(3.5f, 350)]
    [InlineData(2.0f, 200)]
    [InlineData(2.25f, 225)]
    [InlineData(2.5f, 250)]
    [InlineData(3.0f, 300)]
    [InlineData(4.0f, 400)]
    [InlineData(2.625f, null)]
    [InlineData(3.4f, null)]
    [InlineData(float.NaN, null)]
    [InlineData(float.PositiveInfinity, null)]
    public void Requested_scale_comes_only_from_exact_phone_dpr(float dpr, int? scale)
    {
        Assert.Equal(scale, AdaptiveViewportCalibration.ExactWindowsStep(
            TestProfiles.Flagship(dpr)));
    }

    [Theory]
    [InlineData(3.5f, 350)]
    [InlineData(2.0f, 200)]
    [InlineData(2.8125f, 300)]
    [InlineData(2.625f, 250)]
    [InlineData(3.4f, 350)]
    [InlineData(float.NaN, null)]
    public void Nonstandard_dpr_maps_to_the_nearest_windows_step(float dpr, int? scale)
    {
        Assert.Equal(scale, AdaptiveViewportCalibration.WindowsStepFor(
            TestProfiles.Flagship(dpr)));
    }

    [Fact]
    public async Task Nonstandard_dpr_gets_a_verified_1to1_quantized_viewport()
    {
        await using var backend = new FakeDisplayBackend(2048, 3200)
        {
            RecommendedPercent = 100,
            CurrentPercent = 100,
            MaxPercentFor = (width, _) => width >= 1800 ? 300 : 175
        };
        var phone = TestProfiles.Flagship(2.8125f, 1080, 2400, 169);

        var result = await NewCalibration(backend).TryRunAsync(
            phone, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Native);
        Assert.True(result.CanStartGpuWorkspace);
        Assert.Equal(PlanKind.QuantizedViewport, result.Plan.Kind);
        Assert.Equal(1080, result.Plan.PixelW);
        Assert.Equal(2400, result.Plan.PixelH);
        Assert.Equal(1800, result.Monitor.Bounds.Width);
        Assert.Equal(288, result.EffectiveDpi);
        Assert.Contains("288", result.DegradedReason!);
        // Do not create the native width first: on Windows 10 a mode change on
        // the same virtual monitor can be replaced by an unrelated 1920x1080 mode.
        Assert.DoesNotContain(backend.Created,
            r => r.Width == phone.PhysicalW && r.Height == phone.PhysicalH);
        var expectedSerial = PhysicalSize.StableSerial(
            $"{phone.DeviceId}:adaptive:1800x2400@120");
        Assert.All(backend.Created, r => Assert.Equal(expectedSerial, r.Serial));
    }

    [Fact]
    public async Task Nonstandard_quantized_falls_back_when_the_seed_is_too_narrow()
    {
        await using var backend = new FakeDisplayBackend(3456, 2400)
        {
            RecommendedPercent = 100,
            CurrentPercent = 100,
            MaxPercentFor = (width, _) => width >= 2000 ? 300 : 175
        };
        var phone = TestProfiles.Flagship(2.8125f, 1080, 2400, 169);

        var result = await NewCalibration(backend).TryRunAsync(
            phone, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(PlanKind.QuantizedViewport, result!.Plan.Kind);
        Assert.True(result.CanStartGpuWorkspace);
        Assert.Equal(2000, result.Monitor.Bounds.Width);
        Assert.Contains(backend.Created, r => r.Width == 1080);
        Assert.Contains(backend.Created, r => r.Width == 2000);
        Assert.All(backend.Created, r => Assert.Equal(
            PhysicalSize.StableSerial(
                $"{phone.DeviceId}:adaptive:{r.Width}x{r.Height}@120"),
            r.Serial));
        Assert.True(backend.Created.Select(r => r.Serial).Distinct().Count() > 1);
    }

    [Fact]
    public async Task Binary_boundary_search_finds_minimum_even_width_not_fixed_2100()
    {
        await using var backend = new FakeDisplayBackend(2700, 3120)
        {
            RecommendedPercent = 100,
            CurrentPercent = 100,
            MaxPercentFor = (w, _) => w >= 2098 ? 350 : 225
        };
        var phone = TestProfiles.Flagship();
        var result = await NewCalibration(backend).TryRunAsync(phone, null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(PlanKind.VerifiedViewport, result!.Plan.Kind);
        Assert.True(result.Native);
        Assert.Equal(2098, result.Monitor.Bounds.Width); // not 2100 or 2240
        Assert.Equal(1440, result.Plan.PixelW); // phone-sized encode
        Assert.Equal(3120, result.Plan.PixelH);
        Assert.Equal(336, result.EffectiveDpi);
        Assert.Equal(100, result.RestoreScalePercent);
        Assert.Equal(411.4, result.Plan.CanvasDpW, 1);
        Assert.Contains(backend.Created, request => request.Width == 2096);
        Assert.Contains(backend.Created, request => request.Width == 2098);
        Assert.DoesNotContain(backend.SetCalls, x => x > 350);
    }

    [Fact]
    public async Task Landscape_phone_calibrates_height_from_its_DPR_not_a_fixed_2100_width()
    {
        var landscape = TestProfiles.Flagship() with
        {
            DeviceId = "landscape-phone-test",
            PhysicalW = 3120,
            PhysicalH = 1440,
            Orientation = "LANDSCAPE"
        };
        await using var backend = new FakeDisplayBackend(3200, 1750)
        {
            RecommendedPercent = 100,
            MaxPercentFor = (_, h) => h >= 1680 ? 350 : 225
        };
        var result = await NewCalibration(backend).TryRunAsync(
            landscape, null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result!.Native);
        Assert.Equal(PlanKind.VerifiedViewport, result.Plan.Kind);
        Assert.Equal(3120, result.Monitor.Bounds.Width);
        Assert.Equal(1680, result.Monitor.Bounds.Height); // exact height cutoff
        Assert.Equal(3120, result.Plan.PixelW); // 1:1 landscape phone viewport
        Assert.Equal(1440, result.Plan.PixelH);
        Assert.Contains(backend.Created, r => r.Height == 1678);
    }

    [Fact]
    public async Task Verified_cutoff_is_reused_on_reconnect_but_remeasured()
    {
        var phone = TestProfiles.Flagship() with
        {
            DeviceId = "verified-cache-reconnect-test"
        };
        await using var first = new FakeDisplayBackend(2700, 3120)
        {
            RecommendedPercent = 100,
            MaxPercentFor = (width, _) => width >= 2098 ? 350 : 225
        };
        var firstResult = await new AdaptiveViewportCalibration(
            first, new StaticGuard(), new StaticGuard().Snapshot(), new ListLog())
        {
            RememberVerifiedWidth = true,
            InitialSettlingMs = 0,
            RetrySettlingMs = 0
        }.TryRunAsync(phone, null, CancellationToken.None);
        Assert.NotNull(firstResult);
        Assert.Equal(2098, firstResult!.Monitor.Bounds.Width);
        Assert.True(first.Created.Count > 4);

        await using var second = new FakeDisplayBackend(2700, 3120)
        {
            RecommendedPercent = 100,
            MaxPercentFor = (width, _) => width >= 2098 ? 350 : 225
        };
        var secondResult = await new AdaptiveViewportCalibration(
            second, new StaticGuard(), new StaticGuard().Snapshot(), new ListLog())
        {
            RememberVerifiedWidth = true,
            InitialSettlingMs = 0,
            RetrySettlingMs = 0
        }.TryRunAsync(phone, null, CancellationToken.None);
        Assert.NotNull(secondResult);
        Assert.Single(second.Created);
        Assert.Equal(2098, second.Created[0].Width);
        Assert.True(second.ViewportProbeCalls > 0); // cache never bypasses proof
    }

    [Fact]
    public async Task A_336_dpi_monitor_with_96_dpi_PMv2_is_not_accepted()
    {
        await using var backend = new FakeDisplayBackend(2700, 3120)
        {
            RecommendedPercent = 100,
            CurrentPercent = 100,
            MaxPercentFor = (w, _) => w >= 2100 ? 350 : 225,
            ProbeViewportWindowDpi = 96
        };
        var result = await NewCalibration(backend).TryRunAsync(
            TestProfiles.Flagship(), null, CancellationToken.None);
        Assert.Null(result);
        Assert.True(backend.ViewportProbeCalls >= 3);
        Assert.Equal(100, backend.CurrentPercent); // restored after every failed width
        Assert.Contains(350, backend.SetCalls);
        Assert.Contains(100, backend.SetCalls);
    }

    [Fact]
    public async Task The_ruler_must_be_measured_even_if_dpi_looks_right()
    {
        await using var backend = new FakeDisplayBackend(2700, 3120)
        {
            RecommendedPercent = 100,
            MaxPercentFor = (w, _) => w >= 2100 ? 350 : 225,
            ProbeRulerOk = false
        };
        Assert.Null(await NewCalibration(backend).TryRunAsync(
            TestProfiles.Flagship(), null, CancellationToken.None));
        Assert.Equal(100, backend.CurrentPercent);
    }

    [Fact]
    public async Task If_the_native_monitor_allows_the_dpr_it_needs_no_crop()
    {
        await using var backend = new FakeDisplayBackend
        {
            RecommendedPercent = 350,
            CurrentPercent = 350,
            MaxPercentFor = (_, _) => 350
        };
        var result = await NewCalibration(backend).TryRunAsync(
            TestProfiles.Flagship(), null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(PlanKind.Exact, result!.Plan.Kind);
        Assert.Equal(1440, result.Monitor.Bounds.Width);
        Assert.True(result.Native);
        Assert.Null(result.RestoreScalePercent);
        Assert.Empty(backend.SetCalls);
    }

    [Fact]
    public void Exact_pixel_crop_reuses_output_and_correctly_maps_input()
    {
        const int width = 7, height = 3;
        var input = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            input[i * 4] = (byte)(i + 1);
            input[i * 4 + 3] = 255;
        }
        var output = new byte[4 * 3 * 4];
        DprChallengeTheory.CopyViewportBgra(input, width, height,
            new RectI(0, 0, 4, 3), output);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 8, 9, 10, 11, 15, 16, 17, 18 },
            Enumerable.Range(0, 12).Select(n => output[n * 4]).ToArray());

        var geometry = new InputGeometry(new RectI(3440, 0, 1440, 3120), 1440, 3120);
        Assert.Equal((3440, 0), geometry.ToDesktop(0, 0));
        Assert.Equal((4879, 3119), geometry.ToDesktop(1439, 3119));
    }

    [Fact]
    public void Exceeding_shared_frame_budget_fails_without_an_unsafe_mode()
    {
        Assert.Equal(2658, AdaptiveViewportCalibration.MaxEligibleWidth(3120));
        Assert.False(AdaptiveViewportCalibration.CanUseBacking(
            3000, 3120, 1440, 3120));
        Assert.True(AdaptiveViewportCalibration.CanUseBacking(
            2100, 3120, 1440, 3120));
    }
}
