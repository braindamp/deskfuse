using System.Runtime.InteropServices;
namespace FocusWindow.Core.Display;
/// <summary>
/// C# mirror of windows/shared/FocusShared.h. Every number here is asserted against the
/// output of the C++ compiler (driver/tests/contract_test.cpp -> contract.generated.txt) by
/// DriverContractTests, so the two sides cannot drift silently.
/// </summary>
public static class DriverContract
{
    public const uint DeviceType = 0x22; // FILE_DEVICE_UNKNOWN
    public const uint MethodBuffered = 0;
    public const uint FileAnyAccess = 0;
    public const uint FileReadAccess = 1;
    public const uint FileWriteAccess = 2;
    /// <summary>The CTL_CODE macro from winioctl.h.</summary>
    public static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;
    public static readonly uint IoctlSetTarget = CtlCode(DeviceType, 0x800, MethodBuffered, FileWriteAccess);
    public static readonly uint IoctlGetStatus = CtlCode(DeviceType, 0x801, MethodBuffered, FileReadAccess);
    public static readonly uint IoctlRemoveMonitor = CtlCode(DeviceType, 0x802, MethodBuffered, FileWriteAccess);
    public static readonly uint IoctlPing = CtlCode(DeviceType, 0x803, MethodBuffered, FileAnyAccess);
    public const int ProtocolVersion = 2;
    public const int HeartbeatTimeoutMs = 10_000;
    // GUID_DEVINTERFACE_FOCUSIDD {7D4D6F31-9C2E-4B86-9F25-5E9C4A1B8D60}
    public static readonly Guid DeviceInterface = new("7d4d6f31-9c2e-4b86-9f25-5e9c4a1b8d60");
    public const string SectionName = @"Global\FocusWindowSurface.v2";
    public const string FrameEventName = @"Global\FocusWindowFrameReady.v2";
    public const uint SurfaceMagic = 0x53434F46;
    public const uint SurfaceVersion = 2;
    public const uint HeaderBytes = 0x10000;
    public const uint SlotCount = 3;
    public const uint MaxDimension = 4095;
    public const uint MaxPixels = 8_294_400;
    public const uint SlotHeaderBytes = 4096;
    public const uint SlotPixelBytes = MaxPixels * 4;
    public const uint SlotStrideBytes = SlotHeaderBytes + SlotPixelBytes;
    public const uint SectionBytes = HeaderBytes + SlotCount * SlotStrideBytes;
    public const uint PixelFormatBgra8 = 87;
    public const uint ErrFrameTooLarge = 0xE0F00001;
    public const uint ErrUnsupportedFormat = 0xE0F00002;
    // FW_SURFACE_HEADER field offsets
    public const int HdrMagic = 0;
    public const int HdrVersion = 4;
    public const int HdrWidth = 8;
    public const int HdrHeight = 12;
    public const int HdrStride = 16;
    public const int HdrSlotCount = 20;
    public const int HdrSlotStrideBytes = 24;
    public const int HdrPublishedSeq = 32;
    public const int HdrLatestSlot = 40;
    public const int HdrMaxPixels = 44;
    public const int HdrMonitorArrived = 48;
    public const int HdrLastError = 52;
    public const int HdrDropped = 56;
    // FW_SLOT_HEADER field offsets
    public const int SlotSeq = 0;
    public const int SlotQpc = 8;
    public const int SlotWidth = 16;
    public const int SlotHeight = 20;
    [StructLayout(LayoutKind.Sequential)]
    public struct SetTarget
    {
        public uint WidthPx;
        public uint HeightPx;
        public uint RefreshHz;
        public uint PhysWidthMm;
        public uint PhysHeightMm;
        public uint ScaleHintPercentInfoOnly; // diagnostics only; never applied
        public uint Serial;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint MonitorArrived;
        public uint WidthPx;
        public uint HeightPx;
        public uint RefreshHz;
        public ulong FramesPublished;
        public ulong FramesDropped;
        public uint LastError;
        public uint ProtocolVersion;
        public uint SurfaceWin32Error;
        public uint Reserved0;
    }
}
