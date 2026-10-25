#pragma once
#if defined(_KERNEL_MODE)
using uint64_t = unsigned __int64;
using uint32_t = unsigned int;
#ifndef UINT64_MAX
#define UINT64_MAX 0xffffffffffffffffui64
#endif
#ifndef INT64_MAX
#define INT64_MAX 0x7fffffffffffffffi64
#endif
extern "C" unsigned __int64 _udiv128(unsigned __int64, unsigned __int64,
    unsigned __int64, unsigned __int64*);
#pragma intrinsic(_udiv128)
#else
#include <stdint.h>
#include <intrin.h>
#endif

// Shared by the real DDI path and host tests. No floating point, allocation or
// per-tick truncation. Inputs originate in positive QPC and validated PCM format.
namespace deskfuse {
inline uint64_t mul_div(uint64_t a, uint64_t b, uint64_t divisor,
                        uint64_t* remainder = nullptr) {
    uint64_t hi = 0, rem = 0;
    const auto lo = _umul128(a, b, &hi);
    if (!divisor || hi >= divisor) return UINT64_MAX;
    const auto q = _udiv128(hi, lo, divisor, &rem);
    if (remainder) *remainder = rem;
    return q;
}
struct FrameClock {
    uint64_t frames = 0, remainder = 0, tick = 0, frequency = 0;
    uint32_t rate = 0;
    void reset() { frames = remainder = tick = 0; }
    void anchor(uint64_t now) { tick = now; }
    void advance(uint64_t now) {
        if (now < tick || !frequency || !rate) return;
        uint64_t r = 0;
        auto whole = mul_div(now - tick, rate, frequency, &r);
        // Avoid overflowing r + remainder even for an unusual QPC frequency.
        if (r >= frequency - remainder) { ++whole; r -= frequency - remainder; }
        else r += remainder;
        if (whole > UINT64_MAX - frames) return;
        frames += whole; remainder = r; tick = now;
    }
    uint64_t due_hns(uint64_t target) const {
        if (target <= frames) return 1;
        uint64_t hi = 0, r = 0;
        auto lo = _umul128(target - frames, frequency, &hi);
        if (lo < remainder) --hi;
        lo -= remainder;
        if (!rate || hi >= rate) return INT64_MAX;
        auto ticks = _udiv128(hi, lo, rate, &r);
        ticks += r != 0;
        auto hns = mul_div(ticks, 10000000, frequency, &r);
        if (hns >= INT64_MAX) return INT64_MAX;
        return hns + (r != 0) + (hns == 0);
    }
};
}
