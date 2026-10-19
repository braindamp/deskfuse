#pragma once
// FocusWindow IDD v2 shared contract.
// RECONSTRUCTION NOTE: The source scrape recorded only the GUID punctuation change to
// this file, yet every newly created v2 driver/Core source and the successful contract-test
// log require the declarations below. Reconstructed from DriverContract.cs, Driver.cpp,
// driver/tests/contract_test.cpp, and the agent's printed values, NOT verbatim from scrape.
#include <windows.h>
#include <winioctl.h>
#include <initguid.h>

DEFINE_GUID(GUID_DEVINTERFACE_FOCUSIDD,
    0x7d4d6f31, 0x9c2e, 0x4b86, 0x9f, 0x25, 0x5e, 0x9c, 0x4a, 0x1b, 0x8d, 0x60);
#define FOCUSIDD_DEVICE_TYPE 0x22u
#define IOCTL_FOCUSIDD_SET_TARGET CTL_CODE(FOCUSIDD_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_FOCUSIDD_GET_STATUS CTL_CODE(FOCUSIDD_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_FOCUSIDD_REMOVE_MONITOR CTL_CODE(FOCUSIDD_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_FOCUSIDD_PING CTL_CODE(FOCUSIDD_DEVICE_TYPE, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define FOCUSIDD_PROTOCOL_VERSION 2u
#define FOCUSIDD_HEARTBEAT_TIMEOUT_MS 10000u
#define FOCUSIDD_WATCHDOG_PERIOD_MS 2000u // inferred; verify author intent

typedef struct _FOCUSIDD_SET_TARGET {
    UINT32 WidthPx;
    UINT32 HeightPx;
    UINT32 RefreshHz;
    UINT32 PhysWidthMm;
    UINT32 PhysHeightMm;
    UINT32 ScaleFactorHint; // informational, not applied by console IddCx driver
    UINT32 Serial;
} FOCUSIDD_SET_TARGET;

typedef struct _FOCUSIDD_STATUS {
    UINT32 MonitorArrived;
    UINT32 WidthPx;
    UINT32 HeightPx;
    UINT32 RefreshHz;
    UINT64 FramesPublished;
    UINT64 FramesDropped;
    UINT32 LastError;
    UINT32 ProtocolVersion;
    UINT32 SurfaceWin32Error;
    UINT32 Reserved0;
} FOCUSIDD_STATUS;

#define FW_SECTION_NAME L"Global\\FocusWindowSurface.v2"
#define FW_FRAME_EVENT_NAME L"Global\\FocusWindowFrameReady.v2"
#define FW_SURFACE_MAGIC 0x53434F46u
#define FW_SURFACE_VERSION 2u
#define FW_HEADER_BYTES 0x10000u
#define FW_SLOT_COUNT 3u
#define FW_MAX_DIMENSION 4095u
#define FW_MAX_PIXELS 8294400u
#define FW_SLOT_HEADER_BYTES 4096u
#define FW_SLOT_PIXEL_BYTES (FW_MAX_PIXELS * 4u)
#define FW_SLOT_STRIDE_BYTES (FW_SLOT_HEADER_BYTES + FW_SLOT_PIXEL_BYTES)
#define FW_SECTION_BYTES (FW_HEADER_BYTES + FW_SLOT_COUNT * FW_SLOT_STRIDE_BYTES)
#define FW_PIXEL_FORMAT_BGRA8 87u
#define FW_ERR_FRAME_TOO_LARGE 0xE0F00001u
#define FW_ERR_UNSUPPORTED_FORMAT 0xE0F00002u

// Shared frame section: SYSTEM can publish; interactive readers can map read-only.
#define FW_SDDL_SECTION L"D:P(A;;GA;;;SY)(A;;GR;;;IU)"
// The consumer waits on the auto-reset event, which requires SYNCHRONIZE (0x00100000),
// not merely GENERIC_READ. Grant wait-only access, never signal/modify access.
// BA allows elevated interactive diagnostics even when the token is atypical.
#define FW_SDDL_EVENT L"D:P(A;;GA;;;SY)(A;;0x00100000;;;IU)(A;;0x00100000;;;BA)"

// Fixed 96-byte header; offsets are part of the wire-level shared memory ABI.
typedef struct _FW_SURFACE_HEADER {
    volatile UINT32 Magic;              // 0
    UINT32 Version;                     // 4
    UINT32 Width;                       // 8
    UINT32 Height;                      // 12
    UINT32 Stride;                      // 16
    UINT32 SlotCount;                   // 20
    UINT32 SlotStrideBytes;             // 24
    UINT32 PixelFormat;                 // 28
    volatile UINT64 PublishedSeq;       // 32
    volatile UINT32 LatestSlotIndex;    // 40
    UINT32 MaxPixels;                   // 44
    volatile UINT32 MonitorArrived;     // 48
    volatile UINT32 LastError;          // 52
    volatile UINT64 DroppedFrames;      // 56
    UINT32 Reserved[8];                 // 64 -> 96
} FW_SURFACE_HEADER;

typedef struct _FW_SLOT_HEADER {
    volatile UINT64 Seq;                // 0
    UINT64 PresentQpc;                  // 8
    UINT32 Width;                       // 16
    UINT32 Height;                      // 20
    UINT32 Reserved[2];                 // 24 -> 32
} FW_SLOT_HEADER;

#define FW_MONITOR_FRIENDLY_NAME L"FocusPhone"
#define FW_EDID_MANUFACTURER "FOC"
#define FW_EDID_PRODUCT_CODE 0x0046u // inferred; tests only constrain correspondence, not value
