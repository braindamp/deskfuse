// Emits every number the C# agent must agree with, as key=value lines.
// The C# test `DriverContractTests` reads this file and asserts DriverContract.* match,
// so the two sides of the driver<->agent contract cannot silently drift.
#include "../../shared/FocusShared.h"
#include <cstdio>
#include <cstddef>
#define P(name, value) std::printf("%s=%llu\n", name, (unsigned long long)(value))
int main()
{
    P("IOCTL_SET_TARGET", IOCTL_FOCUSIDD_SET_TARGET);
    P("IOCTL_GET_STATUS", IOCTL_FOCUSIDD_GET_STATUS);
    P("IOCTL_REMOVE_MONITOR", IOCTL_FOCUSIDD_REMOVE_MONITOR);
    P("IOCTL_PING", IOCTL_FOCUSIDD_PING);
    P("PROTOCOL_VERSION", FOCUSIDD_PROTOCOL_VERSION);
    P("HEARTBEAT_TIMEOUT_MS", FOCUSIDD_HEARTBEAT_TIMEOUT_MS);
    P("SIZEOF_SET_TARGET", sizeof(FOCUSIDD_SET_TARGET));
    P("SIZEOF_STATUS", sizeof(FOCUSIDD_STATUS));
    P("SIZEOF_SURFACE_HEADER", sizeof(FW_SURFACE_HEADER));
    P("SIZEOF_SLOT_HEADER", sizeof(FW_SLOT_HEADER));
    P("OFFSET_STATUS_FRAMES_PUBLISHED", offsetof(FOCUSIDD_STATUS, FramesPublished));
    P("OFFSET_STATUS_LAST_ERROR", offsetof(FOCUSIDD_STATUS, LastError));
    P("OFFSET_STATUS_SURFACE_ERROR", offsetof(FOCUSIDD_STATUS, SurfaceWin32Error));
    P("SURFACE_MAGIC", FW_SURFACE_MAGIC);
    P("SURFACE_VERSION", FW_SURFACE_VERSION);
    P("HEADER_BYTES", FW_HEADER_BYTES);
    P("SLOT_COUNT", FW_SLOT_COUNT);
    P("MAX_DIMENSION", FW_MAX_DIMENSION);
    P("MAX_PIXELS", FW_MAX_PIXELS);
    P("SLOT_HEADER_BYTES", FW_SLOT_HEADER_BYTES);
    P("SLOT_PIXEL_BYTES", FW_SLOT_PIXEL_BYTES);
    P("SLOT_STRIDE_BYTES", FW_SLOT_STRIDE_BYTES);
    P("SECTION_BYTES", FW_SECTION_BYTES);
    P("PIXEL_FORMAT_BGRA8", FW_PIXEL_FORMAT_BGRA8);
    P("ERR_FRAME_TOO_LARGE", FW_ERR_FRAME_TOO_LARGE);
    P("HDR_OFFSET_MAGIC", offsetof(FW_SURFACE_HEADER, Magic));
    P("HDR_OFFSET_VERSION", offsetof(FW_SURFACE_HEADER, Version));
    P("HDR_OFFSET_WIDTH", offsetof(FW_SURFACE_HEADER, Width));
    P("HDR_OFFSET_HEIGHT", offsetof(FW_SURFACE_HEADER, Height));
    P("HDR_OFFSET_STRIDE", offsetof(FW_SURFACE_HEADER, Stride));
    P("HDR_OFFSET_SLOT_COUNT", offsetof(FW_SURFACE_HEADER, SlotCount));
    P("HDR_OFFSET_PUBLISHED_SEQ", offsetof(FW_SURFACE_HEADER, PublishedSeq));
    P("HDR_OFFSET_LATEST_SLOT", offsetof(FW_SURFACE_HEADER, LatestSlotIndex));
    P("HDR_OFFSET_MAX_PIXELS", offsetof(FW_SURFACE_HEADER, MaxPixels));
    P("HDR_OFFSET_MONITOR_ARRIVED", offsetof(FW_SURFACE_HEADER, MonitorArrived));
    P("HDR_OFFSET_LAST_ERROR", offsetof(FW_SURFACE_HEADER, LastError));
    P("HDR_OFFSET_DROPPED", offsetof(FW_SURFACE_HEADER, DroppedFrames));
    P("SLOT_OFFSET_SEQ", offsetof(FW_SLOT_HEADER, Seq));
    P("SLOT_OFFSET_QPC", offsetof(FW_SLOT_HEADER, PresentQpc));
    P("SLOT_OFFSET_WIDTH", offsetof(FW_SLOT_HEADER, Width));
    P("SLOT_OFFSET_HEIGHT", offsetof(FW_SLOT_HEADER, Height));
    return 0;
}
