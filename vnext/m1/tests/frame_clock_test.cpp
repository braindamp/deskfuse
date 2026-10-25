#include "../../../windows/driver/DeskFuseAudio/src/frame_clock.h"
#include <windows.h>
#include "../../../windows/driver/DeskFuseAudio/src/timing_abi.h"
#include <cstdio>
#include <cstdlib>
#include <initializer_list>
static void require(bool b) { if (!b) std::abort(); }
int main() {
    for (auto rate : {44100u,48000u,96000u}) {
        deskfuse::FrameClock c;
        c.frequency = 10000003; c.rate = rate; c.anchor(500);
        for (unsigned i=1; i<=100000; ++i) c.advance(500ull + i*1001ull);
        uint64_t rem;
        require(c.frames == deskfuse::mul_div(100100000,rate,c.frequency,&rem));
        require(c.remainder == rem);
        const auto frames=c.frames, phase=c.remainder;
        c.anchor(999999999); // resume after a pause; no phase reset
        require(c.frames==frames && c.remainder==phase);
        c.advance(1009999999); // delayed wake: truthfully advances many packets
        require(c.frames-frames >= rate-1);
        c.reset(); require(c.frames==0 && c.remainder==0);
    }
    deskfuse::FrameClock c; c.frequency=10000000; c.rate=48000; c.anchor(0);
    require(c.due_hns(128)==26667);
    c.advance(26666); require(c.frames==127); // never complete early
    c.advance(26667); require(c.frames==128 && c.due_hns(256)==26667);
    c.advance(1000000); require(c.frames==4800 && c.frames/128==37);
    // Wide product exceeds 64 bits, quotient is representable.
    require(deskfuse::mul_div(1000000000000000ull,96000,10000000)==9600000000000ull);
    DeskFuseTimingCapabilities cap={sizeof(cap),DESKFUSE_TIMING_ABI,DESKFUSE_TIMING_BUILD,
        {1,0,0,{0}},DESKFUSE_TIMING_REQUIRED,0};
    require(DeskFuseTimingCompatible(cap));
    --cap.build; require(!DeskFuseTimingCompatible(cap));
    ++cap.build; cap.flags=0; require(!DeskFuseTimingCompatible(cap));
    std::puts("fractional frame clock / delayed progress / pause / capability checks passed");
}
