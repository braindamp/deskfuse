using System.ComponentModel;
using System.Runtime.InteropServices;
using FocusWindow.Agent.Interop;
using FocusWindow.Core.Display;
using Microsoft.Win32.SafeHandles;
namespace FocusWindow.Agent.Display;
/// <summary>
/// The agent's control channel to FocusIdd: find the device interface the driver registered, open it,
/// and speak the IOCTLs in shared/FocusShared.h. Every failure is a DisplayException with a stage and a
/// remediation, so a missing or mismatched driver is a sentence on the phone instead of a black screen.
/// </summary>
internal sealed class DriverDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    public string DevicePath { get; }
    private DriverDevice(SafeFileHandle handle, string path)
    {
        _handle = handle;
        DevicePath = path;
    }
    public static DriverDevice Open()
    {
        var guid = DriverContract.DeviceInterface;
        var set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (set == new IntPtr(-1))
            throw new DisplayException("driver", "Could not enumerate devices: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        try
        {
            var data = new Native.SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<Native.SpDeviceInterfaceData>() };
            if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data))
            {
                throw new DisplayException("driver",
                    "The Focus Window display driver is not installed (no FocusIdd device interface exists).",
                    "Install it from windows/driver (see the README there), then restart the agent.");
            }
            // First call reports the buffer size it needs; the second fills it.
            Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is the size of the FIXED part: 8 on x64.
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero))
                    throw new DisplayException("driver", "Could not read the driver's device path: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                var path = Marshal.PtrToStringUni(buffer + 4) ?? throw new DisplayException("driver", "Empty device path.");
                var handle = Native.CreateFileW(path, Native.GenericRead | Native.GenericWrite, Native.FileShareReadWrite,
                    IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new DisplayException("driver",
                        $"The display driver is present but could not be opened ({new Win32Exception(err).Message}).",
                        err == 5 ? "Another agent may already own it, or the device ACL denies this user."
                                 : "Check Device Manager for a problem code on 'Focus Window Virtual Display'.");
                }
                return new DriverDevice(handle, path);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }
    private unsafe void Ioctl(uint code, void* inBuf, uint inSize, void* outBuf, uint outSize)
    {
        if (!Native.DeviceIoControl(_handle, code, (IntPtr)inBuf, inSize, (IntPtr)outBuf, outSize, out _, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            throw new DisplayException("driver", $"Driver IOCTL 0x{code:X} failed: {new Win32Exception(err).Message} (Win32 {err}).",
                "Restart the agent; if it persists, reinstall the display driver.");
        }
    }
    /// <summary>Returns the driver's protocol version; also counts as a watchdog heartbeat.</summary>
    public unsafe int Ping()
    {
        uint version = 0;
        Ioctl(DriverContract.IoctlPing, null, 0, &version, sizeof(uint));
        return (int)version;
    }
    public unsafe DriverContract.Status GetStatus()
    {
        DriverContract.Status status = default;
        Ioctl(DriverContract.IoctlGetStatus, null, 0, &status, (uint)sizeof(DriverContract.Status));
        return status;
    }
    public unsafe void SetTarget(DriverContract.SetTarget target)
    {
        Ioctl(DriverContract.IoctlSetTarget, &target, (uint)sizeof(DriverContract.SetTarget), null, 0);
    }
    public unsafe void RemoveMonitor() => Ioctl(DriverContract.IoctlRemoveMonitor, null, 0, null, 0);
    public void Dispose() => _handle.Dispose();
}
