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
struct OutputConfigV8 {
    uint32_t size = 32, version = 8, buffer_bursts = 0;
    uint32_t sharing_policy = 0; // 0 shared, 1 exclusive requested
    uint32_t usage_policy = 0; // 0 leave platform default, 1 GAME
    uint32_t reserve_frames = 1440;
    uint32_t reserved0 = 0, reserved1 = 0;
};
static_assert(sizeof(OutputConfigV8) == 32);
struct CallbackMetricsV8 {
    uint64_t size_buckets[6]{}; // <=96, <=240, <=480, <=960, <=1920, larger
    uint64_t measured_calls = 0, total_wall_ns = 0, maximum_wall_ns = 0, clock_failures = 0;
    uint32_t measurement_enabled = 0, reserved = 0;
};
static_assert(sizeof(CallbackMetricsV8) == 88);
constexpr bool valid_reserve(uint32_t frames) noexcept {
    return frames == 1440 || frames == 1200 || frames == 960 || frames == 720 || frames == 480;
}
constexpr bool valid_output_config(const OutputConfigV8& c) noexcept {
    return c.size == sizeof(c) && c.version == 8 && c.reserved0 == 0 && c.reserved1 == 0 &&
        (c.buffer_bursts == 0 || c.buffer_bursts == 2 || c.buffer_bursts == 3) &&
        c.sharing_policy <= 1 && c.usage_policy <= 1 && valid_reserve(c.reserve_frames);
}
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
