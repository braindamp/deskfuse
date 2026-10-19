// Unit test for the driver's EDID synthesis. Builds on Linux:  see tests/run_tests.sh
//
// It does not just check "a block came out": it DECODES the block independently (the
// way an EDID parser would) and asserts the decoded values equal the request, so a
// bit-packing mistake in WriteDetailedTiming cannot hide.
#include "../FocusIdd/Edid.h"
#include "../../shared/FocusShared.h"
#include <cstdio>
#include <cstring>
#include <cmath>
#include <string>
using namespace FocusIdd;
static int g_failed = 0;
static int g_checks = 0;
#define CHECK(cond, ...)                                                        \
    do {                                                                        \
        g_checks++;                                                             \
        if (!(cond)) {                                                          \
            g_failed++;                                                         \
            std::printf("  FAIL %s:%d  %s  -> ", __FILE__, __LINE__, #cond);    \
            std::printf(__VA_ARGS__);                                           \
            std::printf("\n");                                                  \
        }                                                                       \
    } while (0)
struct Decoded
{
    UINT32 clock10k, hActive, hBlank, vActive, vBlank;
    UINT32 hOffset, hSync, vOffset, vSync;
    UINT32 mmW, mmH;
    double refresh;
};
static Decoded DecodeDetailed(const BYTE* D)
{
    Decoded r{};
    r.clock10k = D[0] | (D[1] << 8);
    r.hActive = D[2] | ((D[4] >> 4) << 8);
    r.hBlank = D[3] | ((D[4] & 0xF) << 8);
    r.vActive = D[5] | ((D[7] >> 4) << 8);
    r.vBlank = D[6] | ((D[7] & 0xF) << 8);
    r.hOffset = D[8] | (((D[11] >> 6) & 3) << 8);
    r.hSync = D[9] | (((D[11] >> 4) & 3) << 8);
    r.vOffset = (D[10] >> 4) | (((D[11] >> 2) & 3) << 4);
    r.vSync = (D[10] & 0xF) | ((D[11] & 3) << 4);
    r.mmW = D[12] | ((D[14] >> 4) << 8);
    r.mmH = D[13] | ((D[14] & 0xF) << 8);
    const double total = (double)(r.hActive + r.hBlank) * (double)(r.vActive + r.vBlank);
    r.refresh = (double)r.clock10k * 10000.0 / total;
    return r;
}
static void TestMode(UINT32 w, UINT32 h, UINT32 hz, UINT32 mmW, UINT32 mmH, UINT32 serial)
{
    std::printf("mode %ux%u@%u  %ux%u mm\n", w, h, hz, mmW, mmH);
    const UINT32 effective = EdidBuilder::MaxEncodableRefresh(w, h, hz);
    CHECK(effective == hz, "expected %u Hz to be encodable, got %u", hz, effective);
    EdidParams p{};
    p.WidthPx = w; p.HeightPx = h; p.RefreshHz = effective;
    p.PhysWidthMm = mmW; p.PhysHeightMm = mmH; p.Serial = serial;
    std::strcpy(p.Name, "FocusPhone");
    BYTE e[EdidBuilder::BlockSize];
    EdidBuilder::Build(p, e);
    // --- structure ---
    static const BYTE hdr[8] = { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 };
    CHECK(std::memcmp(e, hdr, 8) == 0, "bad EDID header");
    UINT32 sum = 0;
    for (size_t i = 0; i < 128; i++) sum += e[i];
    CHECK((sum & 0xFF) == 0, "checksum sum=%u", sum);
    CHECK(e[18] == 1 && e[19] == 4, "version %u.%u", e[18], e[19]);
    CHECK(e[126] == 0, "extension count");
    // --- identity ---
    const UINT16 vendor = (UINT16)((e[8] << 8) | e[9]);
    const char m[4] = { (char)('A' + ((vendor >> 10) & 0x1F) - 1),
                        (char)('A' + ((vendor >> 5) & 0x1F) - 1),
                        (char)('A' + (vendor & 0x1F) - 1), 0 };
    CHECK(std::strcmp(m, "FOC") == 0, "vendor '%s'", m);
    CHECK((e[10] | (e[11] << 8)) == FW_EDID_PRODUCT_CODE, "product code");
    const UINT32 ser = e[12] | (e[13] << 8) | (e[14] << 16) | ((UINT32)e[15] << 24);
    CHECK(ser == serial, "serial %u != %u", ser, serial);
    // --- physical size: header cm (rounded up) AND detailed-timing mm ---
    CHECK(e[21] == (mmW + 9) / 10, "H cm %u", e[21]);
    CHECK(e[22] == (mmH + 9) / 10, "V cm %u", e[22]);
    // --- preferred detailed timing ---
    const Decoded d = DecodeDetailed(e + 54);
    CHECK(d.hActive == w, "hActive %u", d.hActive);
    CHECK(d.vActive == h, "vActive %u", d.vActive);
    CHECK(d.mmW == mmW && d.mmH == mmH, "mm %ux%u", d.mmW, d.mmH);
    const UINT32 alignedW = (w + 7) & ~7u;
    CHECK(d.hBlank == 160 + (alignedW - w) && d.hOffset == 48 && d.hSync == 32,
          "H blanking %u/%u/%u (width %u)", d.hBlank, d.hOffset, d.hSync, w);
    CHECK((d.hActive + d.hBlank) == alignedW + 160, "HTotal %u != aligned(%u)+160", d.hActive + d.hBlank, w);
    CHECK(d.vOffset == 3 && d.vSync == 5, "V porch/sync %u/%u", d.vOffset, d.vSync);
    CHECK(d.vBlank >= 14, "vBlank %u", d.vBlank);
    CHECK(d.clock10k * 10000ull <= EdidBuilder::MaxPixelClockHz, "clock %u0kHz", d.clock10k);
    // CVT floors the clock to 250 kHz: refresh must be within 0.5% BELOW the request.
    CHECK(d.refresh <= (double)hz + 0.01 && d.refresh >= (double)hz * 0.995,
          "decoded refresh %.4f for requested %u", d.refresh, hz);
    CHECK((d.clock10k * 10000ull) % 250000ull == 0, "clock not on 250 kHz step");
    // --- vertical blanking duration >= 460 us (the CVT-RB rule) ---
    const double linePeriodUs = 1.0e6 / ((double)(h + d.vBlank) * d.refresh);
    CHECK(linePeriodUs * d.vBlank >= 455.0, "vblank %.1f us", linePeriodUs * d.vBlank);
    // --- descriptors ---
    CHECK(e[72 + 3] == 0xFC, "name descriptor tag");
    std::string name((const char*)e + 72 + 5, 13);
    name = name.substr(0, name.find('\n'));
    CHECK(name == "FocusPhone", "name '%s'", name.c_str());
    CHECK(e[90 + 3] == 0xFD, "range descriptor tag");
    CHECK(e[90 + 6] >= effective, "max V rate %u < %u", e[90 + 6], effective);
    CHECK(e[108 + 3] == 0x10, "dummy descriptor tag");
}
int main()
{
    // The phone sizes the product cares about, incl. the one v1 could not carry (3120 high).
    TestMode(1440, 3120, 60, 70, 152, 0xA1B2C3D4);
    TestMode(1440, 3120, 90, 70, 152, 1);
    TestMode(1440, 3120, 120, 70, 152, 2);
    TestMode(1080, 2400, 60, 68, 151, 3);
    TestMode(1080, 2400, 120, 68, 151, 4);
    TestMode(1812, 2176, 60, 120, 144, 5);   // foldable inner
    TestMode(2560, 1440, 60, 330, 190, 6);
    TestMode(3840, 2160, 60, 600, 340, 7);
    std::printf("encodability limits\n");
    // 1440x3120 @ 144 Hz exceeds the 655.35 MHz EDID clock field -> must clamp, never overflow.
    const UINT32 c144 = EdidBuilder::MaxEncodableRefresh(1440, 3120, 144);
    CHECK(c144 >= 120 && c144 < 144, "1440x3120@144 clamped to %u", c144);
    CHECK(EdidBuilder::ReducedBlanking(1440, 3120, c144).PixelClockHz <= EdidBuilder::MaxPixelClockHz, "clamped clock");
    const UINT32 c4k = EdidBuilder::MaxEncodableRefresh(3840, 2160, 144);
    CHECK(c4k >= 24 && c4k < 144, "3840x2160@144 clamped to %u", c4k);
    CHECK(EdidBuilder::MaxEncodableRefresh(5000, 1000, 60) == 0, "over-wide must be rejected");
    CHECK(EdidBuilder::MaxEncodableRefresh(1000, 4096, 60) == 0, "over-tall must be rejected");
    CHECK(EdidBuilder::MaxEncodableRefresh(0, 1000, 60) == 0, "zero width must be rejected");
    // Determinism: same input -> byte-identical EDID. ParseMonitorDescription relies on a
    // memcmp against the stored block, so any nondeterminism would break arrival.
    EdidParams p{};
    p.WidthPx = 1440; p.HeightPx = 3120; p.RefreshHz = 60; p.PhysWidthMm = 70; p.PhysHeightMm = 152; p.Serial = 9;
    std::strcpy(p.Name, "FocusPhone");
    BYTE a[128], b[128];
    EdidBuilder::Build(p, a);
    EdidBuilder::Build(p, b);
    CHECK(std::memcmp(a, b, 128) == 0, "EDID build is not deterministic");
    std::printf("\n%d checks, %d failed\n", g_checks, g_failed);
    return g_failed == 0 ? 0 : 1;
}
