#pragma once
// =====================================================================================
// Runtime EDID synthesis (VESA E-EDID 1.4, 128-byte base block).
//
// Why synthesize instead of shipping a static block like the sample driver does?
// The EDID's physical size is the one density input a console-session IDD genuinely
// controls: Windows derives the monitor's RECOMMENDED scale from physical size and
// resolution (Microsoft's own sample says drivers must report them "to allow the OS to
// optimize settings like viewing distance and scale factor"). The exact algorithm is
// not documented, so the agent never assumes it — it reads back what Windows chose and
// corrects it (see windows/DENSITY.md).
//
// This file is pure logic (no OS calls) so it is unit-tested on a Linux host:
//   driver/tests/edid_test.cpp
// =====================================================================================
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
namespace FocusIdd
{
    struct EdidParams
    {
        UINT32 WidthPx;
        UINT32 HeightPx;
        UINT32 RefreshHz;           // must already be <= EdidBuilder::MaxEncodableRefresh()
        UINT32 PhysWidthMm;
        UINT32 PhysHeightMm;
        UINT32 Serial;
        char Name[14];              // NUL-terminated, max 13 chars in the descriptor
    };
    struct CvtTiming
    {
        UINT32 PixelClockHz;
        UINT32 HTotal;
        UINT32 HBlank;
        UINT32 HSyncOffset;
        UINT32 HSyncWidth;
        UINT32 VTotal;
        UINT32 VBlank;
        UINT32 VSyncOffset;
        UINT32 VSyncWidth;
    };
    class EdidBuilder
    {
    public:
        static constexpr size_t BlockSize = 128;
        static constexpr UINT32 MaxActive = 4095;            // 12-bit active fields
        static constexpr UINT32 MaxPixelClockHz = 655350000; // 16-bit field, 10 kHz units
        // Complete, checksum-valid 128-byte EDID base block.
        static void Build(const EdidParams& Params, BYTE OutBlock[BlockSize]);
        // VESA CVT 1.2 Reduced Blanking (v1): H blank 160 (48/32/80), V front porch 3,
        // V sync 5, min V back porch 6, V blank >= 460 us, clock FLOORED to 250 kHz.
        static CvtTiming ReducedBlanking(UINT32 Width, UINT32 Height, UINT32 RefreshHz);
        // The EDID detailed-timing pixel clock is 16 bits of 10 kHz units (655.35 MHz).
        // 1440x3120 @ 120 Hz fits (~617 MHz); 144 Hz would not. Returns the highest
        // refresh <= requested whose timing is encodable (0 if even 24 Hz is not).
        static UINT32 MaxEncodableRefresh(UINT32 Width, UINT32 Height, UINT32 Requested);
    };
}
