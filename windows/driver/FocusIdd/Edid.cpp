#include "Edid.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include "../../shared/FocusShared.h"
namespace FocusIdd
{
    namespace
    {
        // sRGB chromaticity (EDID bytes 25..34): R(.640,.330) G(.300,.600) B(.150,.060) W(.3127,.3290)
        void WriteChromaticity(BYTE* Edid)
        {
            auto pack = [](double v) -> UINT32 { return (UINT32)(v * 1024.0 + 0.5) & 0x3FF; };
            const UINT32 rx = pack(0.640), ry = pack(0.330);
            const UINT32 gx = pack(0.300), gy = pack(0.600);
            const UINT32 bx = pack(0.150), by = pack(0.060);
            const UINT32 wx = pack(0.3127), wy = pack(0.3290);
            Edid[25] = (BYTE)(((rx & 0x3) << 6) | ((ry & 0x3) << 4) | ((gx & 0x3) << 2) | (gy & 0x3));
            Edid[26] = (BYTE)(((bx & 0x3) << 6) | ((by & 0x3) << 4) | ((wx & 0x3) << 2) | (wy & 0x3));
            Edid[27] = (BYTE)(rx >> 2);
            Edid[28] = (BYTE)(ry >> 2);
            Edid[29] = (BYTE)(gx >> 2);
            Edid[30] = (BYTE)(gy >> 2);
            Edid[31] = (BYTE)(bx >> 2);
            Edid[32] = (BYTE)(by >> 2);
            Edid[33] = (BYTE)(wx >> 2);
            Edid[34] = (BYTE)(wy >> 2);
        }
        UINT16 VendorId(const char Id[3])
        {
            auto v = [](char c) -> UINT16 { return (UINT16)((c - 'A' + 1) & 0x1F); };
            return (UINT16)((v(Id[0]) << 10) | (v(Id[1]) << 5) | v(Id[2]));
        }
        void WriteDetailedTiming(BYTE* D, const EdidParams& P, const CvtTiming& T)
        {
            const UINT32 clock10k = T.PixelClockHz / 10000;
            D[0] = (BYTE)(clock10k & 0xFF);
            D[1] = (BYTE)((clock10k >> 8) & 0xFF);
            D[2] = (BYTE)(P.WidthPx & 0xFF);
            D[3] = (BYTE)(T.HBlank & 0xFF);
            D[4] = (BYTE)((((P.WidthPx >> 8) & 0xF) << 4) | ((T.HBlank >> 8) & 0xF));
            D[5] = (BYTE)(P.HeightPx & 0xFF);
            D[6] = (BYTE)(T.VBlank & 0xFF);
            D[7] = (BYTE)((((P.HeightPx >> 8) & 0xF) << 4) | ((T.VBlank >> 8) & 0xF));
            D[8] = (BYTE)(T.HSyncOffset & 0xFF);
            D[9] = (BYTE)(T.HSyncWidth & 0xFF);
            D[10] = (BYTE)(((T.VSyncOffset & 0xF) << 4) | (T.VSyncWidth & 0xF));
            D[11] = (BYTE)(
                (((T.HSyncOffset >> 8) & 0x3) << 6) |
                (((T.HSyncWidth >> 8) & 0x3) << 4) |
                (((T.VSyncOffset >> 4) & 0x3) << 2) |
                ((T.VSyncWidth >> 4) & 0x3));
            D[12] = (BYTE)(P.PhysWidthMm & 0xFF);
            D[13] = (BYTE)(P.PhysHeightMm & 0xFF);
            D[14] = (BYTE)((((P.PhysWidthMm >> 8) & 0xF) << 4) | ((P.PhysHeightMm >> 8) & 0xF));
            D[15] = 0;
            D[16] = 0;
            D[17] = 0x1E; // non-interlaced, digital separate sync, +V +H
        }
        void WriteNameDescriptor(BYTE* D, const char* Name)
        {
            std::memset(D, 0, 18);
            D[3] = 0xFC;
            size_t len = std::strlen(Name);
            if (len > 13) len = 13;
            std::memcpy(D + 5, Name, len);
            size_t pos = 5 + len;
            if (pos < 18) D[pos++] = 0x0A;
            for (; pos < 18; pos++) D[pos] = 0x20;
        }
        void WriteRangeLimits(BYTE* D, UINT32 RefreshHz, UINT32 PixelClockHz)
        {
            std::memset(D, 0, 18);
            D[3] = 0xFD;
            D[5] = 24;                                         // min V rate
            D[6] = (BYTE)std::min<UINT32>(RefreshHz, 255);     // max V rate
            D[7] = 30;                                         // min H rate (kHz)
            D[8] = 255;                                        // max H rate (kHz)
            D[9] = (BYTE)std::min<UINT32>((PixelClockHz + 9999999u) / 10000000u, 255); // max clock, 10 MHz units
            D[10] = 0x01;
            D[11] = 0x0A;
            for (int i = 12; i < 18; i++) D[i] = 0x20;
        }
        void WriteDummyDescriptor(BYTE* D)
        {
            std::memset(D, 0, 18);
            D[3] = 0x10;
        }
    }
    CvtTiming EdidBuilder::ReducedBlanking(UINT32 Width, UINT32 Height, UINT32 RefreshHz)
    {
        CvtTiming T = {};
        const UINT32 hActive = (Width + 7) & ~7u; // CVT: multiple of 8
        constexpr UINT32 H_FRONT_PORCH = 48;
        constexpr UINT32 H_SYNC_WIDTH = 32;
        constexpr UINT32 H_BACK_PORCH = 80;
        constexpr UINT32 H_BLANK = H_FRONT_PORCH + H_SYNC_WIDTH + H_BACK_PORCH; // 160
        constexpr UINT32 V_FRONT_PORCH = 3;
        constexpr UINT32 V_SYNC_WIDTH = 5;
        constexpr UINT32 MIN_V_BACK_PORCH = 6;
        constexpr double MIN_V_BLANK_US = 460.0;
        // CVT assumes an 8-aligned active width. The EDID stores the REAL active width
        // (a phone is rarely a multiple of 8), so the rounding must live in the blanking:
        // otherwise HTotal used for the pixel clock (aligned) disagrees with the HTotal a
        // decoder reconstructs (active + blank) and the refresh comes out wrong.
        // (Found by edid_test on a 1812 px wide foldable: 60.10 Hz instead of 60.)
        T.HBlank = H_BLANK + (hActive - Width);
        T.HTotal = Width + T.HBlank;
        T.HSyncOffset = H_FRONT_PORCH;
        T.HSyncWidth = H_SYNC_WIDTH;
        T.VSyncOffset = V_FRONT_PORCH;
        T.VSyncWidth = V_SYNC_WIDTH;
        (void)H_BACK_PORCH;
        // CVT 1.2 RB: H_PERIOD_EST = (1/F - 460us) / V_LINES; VBI_LINES = floor(460us / H_PERIOD_EST) + 1
        const double hPeriodEst = ((1.0 / (double)RefreshHz) - MIN_V_BLANK_US / 1.0e6) / (double)Height;
        UINT32 vbiLines = (UINT32)std::floor((MIN_V_BLANK_US / 1.0e6) / hPeriodEst) + 1;
        const UINT32 minVbi = V_FRONT_PORCH + V_SYNC_WIDTH + MIN_V_BACK_PORCH;
        if (vbiLines < minVbi) vbiLines = minVbi;
        T.VBlank = vbiLines;
        T.VTotal = Height + vbiLines;
        // Clock is FLOORED to a 250 kHz step (CVT), so the true refresh is marginally
        // below the request (<0.1%); the EDID, not the IddCx integer mode, carries it.
        const double rawClock = (double)RefreshHz * (double)T.VTotal * (double)T.HTotal;
        const UINT64 stepped = (UINT64)(std::floor(rawClock / 250000.0)) * 250000ull;
        T.PixelClockHz = (UINT32)std::min<UINT64>(stepped, 0xFFFFFFFFull);
        return T;
    }
    UINT32 EdidBuilder::MaxEncodableRefresh(UINT32 Width, UINT32 Height, UINT32 Requested)
    {
        if (Width == 0 || Height == 0 || Width > MaxActive || Height > MaxActive) return 0;
        for (UINT32 hz = Requested; hz >= 24; hz--)
        {
            if (ReducedBlanking(Width, Height, hz).PixelClockHz <= MaxPixelClockHz) return hz;
        }
        return 0;
    }
    void EdidBuilder::Build(const EdidParams& Params, BYTE OutBlock[BlockSize])
    {
        std::memset(OutBlock, 0, BlockSize);
        BYTE* E = OutBlock;
        static const BYTE Header[8] = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        std::memcpy(E, Header, sizeof(Header));
        const UINT16 vendor = VendorId(FW_EDID_MANUFACTURER);
        E[8] = (BYTE)(vendor >> 8);
        E[9] = (BYTE)(vendor & 0xFF);
        E[10] = (BYTE)(FW_EDID_PRODUCT_CODE & 0xFF);
        E[11] = (BYTE)((FW_EDID_PRODUCT_CODE >> 8) & 0xFF);
        E[12] = (BYTE)(Params.Serial & 0xFF);
        E[13] = (BYTE)((Params.Serial >> 8) & 0xFF);
        E[14] = (BYTE)((Params.Serial >> 16) & 0xFF);
        E[15] = (BYTE)((Params.Serial >> 24) & 0xFF);
        E[16] = 1;   // week
        E[17] = 35;  // 1990 + 35 = 2025
        E[18] = 1;   // EDID 1.4
        E[19] = 4;
        E[20] = 0xA0;                                                    // digital, 8 bpc
        E[21] = (BYTE)std::min<UINT32>((Params.PhysWidthMm + 9) / 10, 255);   // cm, rounded up
        E[22] = (BYTE)std::min<UINT32>((Params.PhysHeightMm + 9) / 10, 255);
        E[23] = 120;                                                     // gamma 2.20
        E[24] = 0x0E;
        WriteChromaticity(E);
        for (int i = 38; i < 54; i += 2) { E[i] = 0x01; E[i + 1] = 0x01; } // unused standard timings
        const CvtTiming Timing = ReducedBlanking(Params.WidthPx, Params.HeightPx, Params.RefreshHz);
        WriteDetailedTiming(E + 54, Params, Timing);
        WriteNameDescriptor(E + 72, Params.Name);
        WriteRangeLimits(E + 90, Params.RefreshHz, Timing.PixelClockHz);
        WriteDummyDescriptor(E + 108);
        E[126] = 0;
        UINT32 sum = 0;
        for (int i = 0; i < 127; i++) sum += E[i];
        E[127] = (BYTE)((256 - (sum & 0xFF)) & 0xFF);
    }
}
