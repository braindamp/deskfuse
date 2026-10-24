#pragma once
#include <cstdint>
#include <limits>
namespace df_audio {
struct OutputConfigV7 {
    uint32_t size = 16;
    uint32_t version = 7;
    uint32_t buffer_bursts = 0; // 0=platform default; 2/3=explicit experiment
    uint32_t reserved = 0;
};
static_assert(sizeof(OutputConfigV7) == 16);
constexpr bool valid_output_config(const OutputConfigV7& c) noexcept {
    return c.size == sizeof(OutputConfigV7) && c.version == 7 && c.reserved == 0 &&
        (c.buffer_bursts == 0 || c.buffer_bursts == 2 || c.buffer_bursts == 3);
}
constexpr int32_t requested_buffer_frames(uint32_t bursts, int32_t reported_burst) noexcept {
    if (bursts == 0) return 0;
    if ((bursts != 2 && bursts != 3) || reported_burst <= 0) return -1;
    const uint64_t frames = uint64_t(bursts) * static_cast<uint32_t>(reported_burst);
    return frames <= static_cast<uint64_t>(std::numeric_limits<int32_t>::max()) ?
        static_cast<int32_t>(frames) : -1;
}
} // namespace df_audio
