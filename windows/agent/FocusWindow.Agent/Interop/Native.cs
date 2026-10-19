using System.Runtime.InteropServices;
using FocusWindow.Core.Display;
using Microsoft.Win32.SafeHandles;
namespace FocusWindow.Agent.Interop;
/// <summary>All P/Invoke declarations. Struct layouts live in Core (Win32Layout) so they are unit-tested.</summary>
internal static class Native
{
    // ---- display config -------------------------------------------------------------------------
    public const uint QdcAllPaths = 0x1;
    public const uint QdcOnlyActivePaths = 0x2;
    public const int ErrorInsufficientBuffer = 122;
    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags, ref uint numPaths, [Out] Win32Layout.PathInfo[] paths,
        ref uint numModes, [Out] Win32Layout.ModeInfo[] modes, IntPtr currentTopologyId);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetSourceName(ref Win32Layout.SourceDeviceName packet);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetTargetName(ref Win32Layout.TargetDeviceName packet);
    // UNDOCUMENTED packet types -3 / -4 (see Win32Layout.DpiScaleGet).
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    public static extern int GetDpiScale(ref Win32Layout.DpiScaleGet packet);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    public static extern int SetDpiScale(ref Win32Layout.DpiScaleSet packet);
    // ---- monitors / DPI -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);
    /// <summary>dpiType 0 = MDT_EFFECTIVE_DPI; supplemental diagnostic only. Microsoft
    /// warns against GetDpiForMonitor on a per-monitor-aware thread. Never use this
    /// alone to certify native density; use the PMv2 probe GetDpiForWindow + ruler.</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    // DPI_AWARENESS_CONTEXT pseudo-handles
    public static readonly IntPtr CtxUnaware = new(-1);
    public static readonly IntPtr CtxSystemAware = new(-2);
    public static readonly IntPtr CtxPerMonitor = new(-3);
    public static readonly IntPtr CtxPerMonitorV2 = new(-4);
    public static readonly IntPtr CtxUnawareGdiScaled = new(-5);
    // ---- input ----------------------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, [In] Win32Layout.Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code, uint mapType);
    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;
    // ---- device interface (driver control channel) ----------------------------------------------
    public const uint DigcfPresent = 0x2;
    public const uint DigcfDeviceInterface = 0x10;
    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr set, IntPtr deviceInfoData, ref Guid interfaceClass, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize, IntPtr outBuffer, uint outSize,
        out uint returned, IntPtr overlapped);
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareReadWrite = 0x3;
    public const uint OpenExisting = 3;
}
