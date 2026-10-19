using System.Runtime.InteropServices;
namespace FocusWindow.Core.Display;
/// <summary>
/// Win32 structure layouts used by the Windows agent. They live in Core (not in the Windows-only
/// project) for one reason: they are blittable, so their sizes can be asserted by a unit test on
/// any OS. A wrong size here fails SILENTLY on Windows (garbage DPI, a rejected IOCTL), so
/// InteropLayoutTests pins every one against the documented Win32 sizes.
/// </summary>
public static class Win32Layout
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }
    /// <summary>DISPLAYCONFIG_PATH_SOURCE_INFO: 20 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }
    /// <summary>DISPLAYCONFIG_PATH_TARGET_INFO: 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }
    /// <summary>DISPLAYCONFIG_PATH_INFO: 72 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
        public const uint PathActive = 0x1;
        public bool IsActive => (Flags & PathActive) != 0;
    }
    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO: 64 bytes. The agent never interprets modes (it only needs the
    /// path list), so the union is represented by padding to the documented size.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }
    /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER: 20 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }
    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME: 84 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }
    /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME: 420 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }
    /// <summary>
    /// The UNDOCUMENTED per-monitor DPI-scale GET packet (device-info type -3), as reverse engineered by
    /// the community and used by Windows Settings. Min/Cur/Max are indices RELATIVE to the recommended
    /// scale step. 32 bytes. Source for the layout and the type numbers:
    /// github.com/lihas/windows-DPI-scaling-sample (asserts sizeof == 0x20 / 0x18).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DpiScaleGet
    {
        public DeviceInfoHeader Header;
        public int MinScaleRel;
        public int CurScaleRel;
        public int MaxScaleRel;
    }
    /// <summary>The undocumented SET packet (device-info type -4). 24 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DpiScaleSet
    {
        public DeviceInfoHeader Header;
        public int ScaleRel;
    }
    public const int DeviceInfoGetSourceName = 1;
    public const int DeviceInfoGetTargetName = 2;
    public const int DeviceInfoGetDpiScale = -3;   // undocumented
    public const int DeviceInfoSetDpiScale = -4;   // undocumented
    // ---- SendInput -----------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
    }
    /// <summary>INPUT: 40 bytes on x64 (4 type + 4 padding + 32 union). SendInput rejects any other cbSize.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion U;
    }
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;
}
