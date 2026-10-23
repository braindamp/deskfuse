#pragma once
// DeskFuse M2b: the Android OUTPUT callback is the only playback clock
// authority. This bounded controller adjusts source samples consumed per
// output frame, NOT QUIC pacing, WASAPI source timestamps, or AAudio rate.
//
// A causal linear interpolator consumes Q32 source-frame positions.
// No heap allocation, I/O, locking, or system calls are performed here.
// One instance is accessed only on AAudio's data callback thread.

#include <algorithm>
#include <cstdint>

namespace df_audio {

constexpr uint32_t kChannels = 2;
constexpr uint32_t kBlockFrames = 240;
constexpr uint64_t kRingFrames = kBlockFrames * 16;   // 80 ms absolute cap
constexpr uint64_t kPrimeFrames = kBlockFrames * 6;  // 30 ms
constexpr uint64_t kTargetFrames = kBlockFrames * 6; // 30 ms
constexpr int32_t kMaxCorrectionPpm = 400;
constexpr uint64_t kOneQ32 = uint64_t{1} << 32;

struct DriftFollower {
    // Callback-private state; resets only after genuine output starvation.
    uint32_t fraction_q32 = 0;
    int64_t filtered_error_q8 = 0;
    int64_t integral_sum = 0;
    int32_t ppm = 0;

    void reset() noexcept {
        fraction_q32 = 0;
        filtered_error_q8 = 0;
        integral_sum = 0;
        ppm = 0;
    }

    [[nodiscard]] uint64_t step(uint64_t available) noexcept {
        const int64_t error = static_cast<int64_t>(available) -
                              static_cast<int64_t>(kTargetFrames);
        // Smooth jitter and batching; do NOT feed QUIC ACK timing into this.
        // Approx. 250-500ms at typical AAudio callback sizes.
        filtered_error_q8 += ((error * 256) - filtered_error_q8) / 64;
        const int64_t smooth_frames = filtered_error_q8 / 256;
        // A small integral term learns persistent source/sink clock offset.
        integral_sum = std::clamp(
            integral_sum + smooth_frames,
            -int64_t{200} * 4096, int64_t{200} * 4096);
        const int64_t requested_ppm = smooth_frames / 2 + integral_sum / 4096;
        ppm = static_cast<int32_t>(std::clamp(
            requested_ppm, -int64_t{kMaxCorrectionPpm},
            int64_t{kMaxCorrectionPpm}));
        const int64_t adjustment =
            static_cast<int64_t>(kOneQ32) * ppm / 1'000'000;
        return static_cast<uint64_t>(static_cast<int64_t>(kOneQ32) + adjustment);
    }

    [[nodiscard]] static int16_t sample(
        int16_t a, int16_t b, uint32_t fraction) noexcept {
        const int64_t difference =
            static_cast<int64_t>(b) - static_cast<int64_t>(a);
        const int64_t interpolated =
            static_cast<int64_t>(a) + ((difference * fraction) >> 32);
        return static_cast<int16_t>(interpolated);
    }

    // Caller owns read_pos and checks available >= 2. Output frame is
    // interpolated at (read_pos + fraction_q32), then local read_pos is
    // advanced by 0, 1, or 2. Consumer publishes read_pos AFTER the entire
    // callback, avoiding producer reuse during interpolation.
    void consume_one(
        const int16_t* ring, uint64_t& read_pos, int16_t* output,
        uint64_t step_q32) noexcept {
        const uint64_t p0 = read_pos % kRingFrames;
        const uint64_t p1 = (p0 + 1) % kRingFrames;
        for (uint32_t channel = 0; channel < kChannels; ++channel) {
            output[channel] = sample(
                ring[p0 * kChannels + channel],
                ring[p1 * kChannels + channel],
                fraction_q32);
        }
        const uint64_t next =
            static_cast<uint64_t>(fraction_q32) + step_q32;
        read_pos += next >> 32;
        fraction_q32 = static_cast<uint32_t>(next);
    }
};
}  // namespace df_audio
