using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

// No inheritable handles: only the broker owns this job. Closing/killing it
// prevents orphan acquisition after native-owner death or broker death.
internal sealed class CaptureJob : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly List<SafeFileHandle> _children = [];
    public CaptureJob()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        { _job.Dispose(); throw new Win32Exception(); }
    }
    public void Add(uint childPid, uint pipeClientPid)
    {
        if (_children.Count != 0) throw new InvalidOperationException("Capture child already registered.");
        var child = OpenProcess(0x0100 | 0x0400 | 0x0001 | 0x00100000, false, childPid);
        if (child.IsInvalid) { child.Dispose(); throw new Win32Exception(); }
        try
        {
            // Query the opened object, avoiding a PID-reuse race in snapshots.
            var info = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(child, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != 0 || info.ParentProcessId.ToUInt64() != pipeClientPid)
                throw new InvalidOperationException("Capture must be a direct child of the pipe owner.");
            if (!AssignProcessToJobObject(_job, child)) throw new Win32Exception();
            _children.Add(child);
        }
        catch { child.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_job.IsClosed) return;
        if (!TerminateJobObject(_job, 1)) throw new Win32Exception();
        foreach (var child in _children)
        {
            if (WaitForSingleObject(child, 5000) != 0)
                throw new InvalidOperationException("Capture termination not confirmed; endpoint retained.");
        }
        foreach (var child in _children) child.Dispose();
        _children.Clear();
        _job.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
    { public IntPtr ExitStatus, Peb, Affinity, Priority; public UIntPtr ProcessId, ParentProcessId; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint WaitForSingleObject(SafeFileHandle process, uint ms);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeFileHandle process, int type, ref ProcessBasicInformation info, int size, out int returned);
}
