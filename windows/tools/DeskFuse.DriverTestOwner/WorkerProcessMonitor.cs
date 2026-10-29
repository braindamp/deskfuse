using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using DeskFuse.DriverTestOwner.Core;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.DriverTestOwner;

/// <summary>
/// Holds a process object for one previously registered worker incarnation.
/// Reopening by PID is permitted only after matching its creation time. A PID
/// alone, an exit code, or an untrusted caller's assertion is never an exit observation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WorkerProcessMonitor : IDisposable
{
    private const uint QueryLimitedInformation = 0x1000, Synchronize = 0x00100000;
    private const uint WaitObject0 = 0, WaitTimeout = 258, WaitFailed = 0xffffffff;
    private readonly SafeFileHandle process;
    internal WorkerIdentity Identity { get; }

    private WorkerProcessMonitor(WorkerIdentity identity, SafeFileHandle process)
        => (Identity, this.process) = (identity, process);

    internal static WorkerProcessMonitor OpenExact(WorkerIdentity identity) => Open(identity, false);

    internal static WorkerProcessMonitor FromStartedProcess(Process started, Guid nonce, WorkerRole role)
    {
        if (role is not (WorkerRole.Creator or WorkerRole.Recovery) || nonce == Guid.Empty)
            throw new InvalidOperationException("Only a newly launched native worker is accepted here.");
        // Process.Start retains the original process object. Duplicate that
        // object even if a quick child has already exited; never reopen its PID.
        if (!DuplicateHandle(new nint(-1), started.SafeHandle, new nint(-1), out var held,
                QueryLimitedInformation | Synchronize, false, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to retain the launched worker object.");
        try
        {
            var pid = GetProcessId(held);
            if (pid == 0 || !GetProcessTimes(held, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to identify the launched worker.");
            var time = unchecked(((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime);
            return new(new(checked((int)pid), time, nonce, role), held);
        }
        catch { held.Dispose(); throw; }
    }

    internal static WorkerProcessMonitor OpenProbe(WorkerIdentity identity, string expectedImage)
    {
        if (identity.Role != WorkerRole.Probe) throw new InvalidOperationException("Only the owned probe may be terminated.");
        var monitor = Open(identity, true);
        try
        {
            var image = new StringBuilder(32768);
            var length = (uint)image.Capacity;
            if (!QueryFullProcessImageNameW(monitor.process, 0, image, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify the parked probe image.");
            if (!string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The parked process is not the approved probe image.");
            return monitor;
        }
        catch { monitor.Dispose(); throw; }
    }

    private static WorkerProcessMonitor Open(WorkerIdentity identity, bool terminate)
    {
        identity.Validate();
        var process = OpenProcess(QueryLimitedInformation | Synchronize | (terminate ? 1u : 0u), false, checked((uint)identity.Pid));
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            process.Dispose();
            throw new Win32Exception(error, "Unable to observe the registered worker process.");
        }
        try
        {
            if (GetProcessId(process) != identity.Pid || !GetProcessTimes(process, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify worker process identity.");
            var creationTime = unchecked(((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime);
            if (creationTime != identity.CreationFileTime)
                throw new InvalidDataException("The PID now refers to another process incarnation.");
            return new WorkerProcessMonitor(identity, process);
        }
        catch { process.Dispose(); throw; }
    }

    internal void StopProbe()
    {
        if (Identity.Role != WorkerRole.Probe) throw new InvalidOperationException("This monitor is observation-only.");
        if (!HasExited() && !TerminateProcess(process, 3))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to stop the exact owned probe.");
        // TerminateProcess success is not a process-exit observation.
    }

    /// <summary>True only after the retained process object is signaled.</summary>
    internal bool HasExited()
    {
        ObjectDisposedException.ThrowIf(process.IsClosed, this);
        return WaitForSingleObject(process, 0) switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to observe worker exit."),
            _ => throw new InvalidDataException("Unexpected worker wait result.")
        };
    }

    internal bool ObserveExit(RunCoordinator owner)
    {
        if (!HasExited()) return false;
        // The coordinator additionally verifies that this exact nonce/role and
        // incarnation belongs to its registered run before journaling the exit.
        owner.ObserveExitedWorker(Identity);
        return true;
    }

    public void Dispose() => process.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeFileHandle process, out FILETIME creation, out FILETIME exit,
        out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeFileHandle process, uint flags, StringBuilder image, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeProcessHandle source, nint targetProcess,
        out SafeFileHandle target, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}

/// <summary>Suspend-inclusive uptime; never interprets a reboot as a renewed proof.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDriverTestClock : IDriverTestClock
{
    public long Milliseconds => checked((long)GetTickCount64());
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
