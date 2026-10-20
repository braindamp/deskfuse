namespace FocusWindow.Core.Display;

/// <summary>
/// Production version of the POC's real-scale readback/reapply gate.
/// Scope is ONE already-identified virtual monitor, never the physical
/// desktop, registry or a driver ACL. No "success" until the applied
/// value remains stable across readbacks.
/// </summary>
public static class ScaleEnforcer
{
    public sealed record Result(bool Changed, int Before, int After);

    public static async Task<Result> RequireAsync(IDisplayBackend backend,
        MonitorInfo virtualMonitor, int desiredPercent, CancellationToken ct)
    {
        var start = backend.ReadScaleCaps(virtualMonitor) ??
            throw new DisplayException("SCALE_CAPS_UNAVAILABLE",
                "Windows cannot read this virtual monitor's current scale.");
        if (!start.Allows(desiredPercent))
            throw new DisplayException("SCALE_REAPPLY_FAILED",
                $"Requested {desiredPercent}% exceeds the identified virtual monitor's " +
                $"cap {start.MaxPercent}%. No physical monitor setting was changed.");
        bool changed = start.CurrentPercent != desiredPercent;

        for (var attempt = 0; attempt < 2; ++attempt)
        {
            var state = backend.ReadScaleCaps(virtualMonitor);
            if (state?.CurrentPercent != desiredPercent)
            {
                var applied = backend.TrySetScale(virtualMonitor, desiredPercent);
                if (!applied.Ok)
                {
                    if (attempt == 1)
                        throw new DisplayException("SCALE_REAPPLY_FAILED",
                            $"Virtual monitor {virtualMonitor.GdiName} could not apply " +
                            $"{desiredPercent}%: {applied.Detail}");
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    continue;
                }
                changed = true;
            }

            var deadline = Environment.TickCount64 + 8000;
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var seen = backend.ReadScaleCaps(virtualMonitor);
                if (seen?.CurrentPercent == desiredPercent) break;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }

            // DisplayConfigSET can report success before a temporary 100%
            // reset. Verify a second time after Windows settles.
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var verified = backend.ReadScaleCaps(virtualMonitor);
            if (verified?.CurrentPercent == desiredPercent)
                return new Result(changed, start.CurrentPercent, verified.CurrentPercent);
        }
        throw new DisplayException("SCALE_REAPPLY_FAILED",
            $"Windows did not retain {desiredPercent}% on virtual monitor " +
            $"{virtualMonitor.GdiName}; refusing to certify native DPI.");
    }
}
