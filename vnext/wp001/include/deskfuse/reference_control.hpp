#pragma once
#include <algorithm>
#include <cmath>

namespace df {
// Not the product ASRC controller. Deterministic plant/sign reference to
// prevent negative feedback becoming positive feedback during integration.
// rho = source frames consumed per output frame.
struct ReferenceControl {
    static constexpr double kPhaseTrimLimit = 200e-6;
    [[nodiscard]] static double ratio(double source_frames_per_s,
                                       double output_frames_per_s,
                                       double inventory_error_seconds) noexcept {
        const double rho_ff = source_frames_per_s / output_frames_per_s;
        // Overfull source inventory => consume MORE source frames per output.
        const double trim = std::clamp(inventory_error_seconds / 60.0,
                                       -kPhaseTrimLimit, kPhaseTrimLimit);
        return rho_ff * (1.0 + trim);
    }
    [[nodiscard]] static double inventory_change_frames_per_s(
        double source_frames_per_s, double output_frames_per_s, double rho) noexcept {
        return source_frames_per_s - output_frames_per_s * rho;
    }
};
} // namespace df
