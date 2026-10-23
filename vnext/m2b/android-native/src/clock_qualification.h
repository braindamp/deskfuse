#pragma once
// MP-001 prototype controller for deterministic qualification, NOT a
// production-qualified streaming ASRC. Output callback elapsed frames are
// the only integration timebase; QUIC arrival/ACKs never advance this clock.
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace df_audio {
constexpr double kClockRate = 48000.0;
struct ClockTuning {
    double filter_tau_seconds = 1.0;          // approximately 0.16 Hz
    double kp_ppm_per_frame = 0.8;
    double ki_ppm_per_frame_second = 0.25;
    double max_correction_ppm = 400.0;
    double max_slew_ppm_per_second = 100.0;
    double max_filter_step_frames = 240.0;
    double disturbance_threshold_frames = 144.0;
    double disturbance_hold_seconds = 0.4;
    double integral_bound_ppm = 400.0;
};

class TimeBasedFollower {
public:
    explicit TimeBasedFollower(ClockTuning settings = {}) noexcept : tuning_(settings) {}
    void reset() noexcept {
        filtered_error_frames_ = 0.0;
        integral_ppm_ = 0.0;
        correction_ppm_ = 0.0;
        hold_seconds_ = 0.0;
        disturbances_ = 0;
        saturated_seconds_ = 0.0;
        initialized_ = false;
    }
    // nframes is actual callback demand. Compare occupancy AFTER projecting
    // consumption, so 48-frame and 960-frame callbacks have the same target.
    double update(uint32_t nframes, int64_t available_source_frames,
                  int64_t target_source_frames) noexcept {
        if (!nframes) return correction_ppm_;
        const double dt = static_cast<double>(nframes) / kClockRate;
        const double error = static_cast<double>(
            available_source_frames - static_cast<int64_t>(nframes) -
            target_source_frames);
        if (!initialized_) { filtered_error_frames_ = error; initialized_ = true; }
        const double delta = error - filtered_error_frames_;
        const bool disturbance = std::abs(delta) >
            tuning_.disturbance_threshold_frames;
        if (disturbance) {
            hold_seconds_ = tuning_.disturbance_hold_seconds;
            ++disturbances_;
        }
        // Bound the effect of an arrival burst/loss on the measured trend.
        // Hold the integral after a large measurement step; do not learn it
        // as an enduring source-clock-rate difference.
        const double limited = std::clamp(delta,
            -tuning_.max_filter_step_frames,
             tuning_.max_filter_step_frames);
        const double alpha = dt / (tuning_.filter_tau_seconds + dt);
        filtered_error_frames_ += alpha * limited;
        hold_seconds_ = std::max(0.0, hold_seconds_ - dt);
        const double proportional =
            tuning_.kp_ppm_per_frame * filtered_error_frames_;
        if (!disturbance && hold_seconds_ == 0.0) {
            const double proposed = integral_ppm_ +
                tuning_.ki_ppm_per_frame_second *
                filtered_error_frames_ * dt;
            const double raw = proportional + proposed;
            // Conditional integration: no winding further into saturation.
            if (!((raw > tuning_.max_correction_ppm &&
                    filtered_error_frames_ > 0) ||
                  (raw < -tuning_.max_correction_ppm &&
                    filtered_error_frames_ < 0))) {
                integral_ppm_ = std::clamp(proposed,
                    -tuning_.integral_bound_ppm,
                     tuning_.integral_bound_ppm);
            } else {
                saturated_seconds_ += dt;
            }
        }
        const double command = std::clamp(proportional + integral_ppm_,
            -tuning_.max_correction_ppm,
             tuning_.max_correction_ppm);
        const double max_delta = tuning_.max_slew_ppm_per_second * dt;
        correction_ppm_ += std::clamp(command - correction_ppm_,
            -max_delta, max_delta);
        return correction_ppm_;
    }
    double ppm() const noexcept { return correction_ppm_; }
    double filtered_error() const noexcept { return filtered_error_frames_; }
    uint64_t disturbances() const noexcept { return disturbances_; }
    double saturated_seconds() const noexcept { return saturated_seconds_; }
private:
    ClockTuning tuning_{};
    double filtered_error_frames_ = 0.0;
    double integral_ppm_ = 0.0;
    double correction_ppm_ = 0.0;
    double hold_seconds_ = 0.0;
    uint64_t disturbances_ = 0;
    double saturated_seconds_ = 0.0;
    bool initialized_ = false;
};
} // namespace df_audio
