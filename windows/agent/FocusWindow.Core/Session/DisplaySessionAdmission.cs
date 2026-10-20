namespace FocusWindow.Core.Session;

/// <summary>
/// Admission boundary between authenticated display sessions and the actual
/// virtual-display backend. The scheduler decides how many independent display
/// resources can be owned without any sessions sharing a destructive teardown.
/// This is NOT a virtual monitor implementation.
/// </summary>
public interface IDisplaySessionAdmission
{
    /// <returns>An owned lease, or null when the backend has no safe display resource.</returns>
    IDisposable? TryAcquire(string enrolledDeviceId);
}

/// <summary>
/// Existing FocusIdd backend uses host-global create/remove operations, so it
/// must remain exclusive. A future multi-monitor backend can supply a different
/// admission provider alongside distinct per-session display resources.
/// </summary>
public sealed class ExclusiveVirtualDisplayAdmission : IDisplaySessionAdmission
{
    private int _occupied;

    public IDisposable? TryAcquire(string enrolledDeviceId)
    {
        if (string.IsNullOrWhiteSpace(enrolledDeviceId))
            throw new ArgumentException("Authenticated device ID required", nameof(enrolledDeviceId));
        if (Interlocked.CompareExchange(ref _occupied, 1, 0) != 0)
            return null;
        return new Lease(this);
    }

    private sealed class Lease(ExclusiveVirtualDisplayAdmission owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Exchange(ref owner._occupied, 0);
        }
    }
}
