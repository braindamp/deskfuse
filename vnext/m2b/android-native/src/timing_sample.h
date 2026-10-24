#pragma once
#include <cstdint>
#include <cstddef>
namespace df_audio {
// Additive v7 symbol, never used from the callback. Frames include startup
// and recovery silence. The owner samples one stream throughout the bracket.
struct TimingSampleV7 {
    uint32_t size = 104, version = 7;
    uint64_t output_generation = 0;
    int64_t before_ns = 0, after_ns = 0;
    int64_t written_before = 0, written_after = 0;
    int64_t timestamp_frame = 0, timestamp_ns = 0;
    int32_t clock_result = 0, timestamp_result = 0;
    int32_t sample_rate = 0, state_before = 0, state_after = 0;
    int32_t xrun_count = 0, buffer_frames = 0, capacity_frames = 0;
    int32_t device_id = 0, reserved = 0;
};
static_assert(sizeof(TimingSampleV7) == 104);
static_assert(offsetof(TimingSampleV7, timestamp_frame) == 48);
static_assert(offsetof(TimingSampleV7, clock_result) == 64);
} // namespace df_audio
