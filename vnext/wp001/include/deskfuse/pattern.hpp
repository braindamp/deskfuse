#pragma once
#include "deskfuse/frame.hpp"
#include <cstdint>

namespace df {

// Defined wrapping arithmetic, deterministic across Windows/Linux/Android.
[[nodiscard]] constexpr std::int16_t pattern_sample(std::uint64_t frame, std::uint32_t channel) noexcept {
    std::uint64_t x = frame + UINT64_C(0x9E3779B97F4A7C15) * (static_cast<std::uint64_t>(channel) + 1U);
    x = (x ^ (x >> 30U)) * UINT64_C(0xBF58476D1CE4E5B9);
    x = (x ^ (x >> 27U)) * UINT64_C(0x94D049BB133111EB);
    x ^= x >> 31U;
    const auto low = static_cast<std::uint16_t>(x);
    const auto signed_value = (low <= 32767U)
        ? static_cast<std::int32_t>(low)
        : static_cast<std::int32_t>(low) - 65536;
    return static_cast<std::int16_t>(signed_value);
}

inline bool fill_pattern(PcmBlock& block, Generation epoch, FrameRange range) noexcept {
    if (!range.valid() || range.count > kMaxBlockFrames) return false;
    block.generation = epoch;
    block.range = range;
    for (std::uint32_t i = 0; i < range.count; ++i) {
        for (std::uint32_t c = 0; c < kChannels; ++c) {
            block.pcm[static_cast<std::size_t>(i) * kChannels + c] = pattern_sample(range.first + i, c);
        }
    }
    return true;
}

[[nodiscard]] inline bool verify_pattern(const std::int16_t* pcm, FrameRange range) noexcept {
    if (!pcm || !range.valid() || range.count > kMaxBlockFrames) return false;
    for (std::uint32_t i = 0; i < range.count; ++i) {
        for (std::uint32_t c = 0; c < kChannels; ++c) {
            if (pcm[static_cast<std::size_t>(i) * kChannels + c] != pattern_sample(range.first + i, c)) return false;
        }
    }
    return true;
}

} // namespace df
