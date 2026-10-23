// Comparison evidence ONLY. Linear interpolation is not a production ASRC.
// A half-frame offset has normalized magnitude |cos(pi*f/fs)|, so output
// above 16kHz is strongly attenuated. This test documents the defect rather
// than blessing it as passing an undisclosed production acceptance limit.
#include "adaptive_clock.h"
#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <numbers>
int main() {
    constexpr double fs = 48000.0;
    constexpr auto half_q32 = uint32_t{0x80000000u};
    auto gain_db = [](double f) {
        const double angle = std::numbers::pi * f / fs;
        return 20.0 * std::log10(std::abs(std::cos(angle)));
    };
    const double g1k = gain_db(1000.0);
    const double g10k = gain_db(10000.0);
    const double g20k = gain_db(20000.0);
    if (!(g1k > -0.1 && g10k < -1.5 && g20k < -10.0))
        throw std::runtime_error("linear spectral comparator changed");
    const auto mid = df_audio::DriftFollower::sample(-12000, 12000, half_q32);
    if (mid != 0) throw std::runtime_error("half-frame interpolation phase mismatch");
    std::printf("COMPARATOR_ONLY linear half-frame transfer: 1kHz=%.3fdB 10kHz=%.3fdB 20kHz=%.3fdB; NOT A PRODUCTION ASRC PASS\n",
                g1k, g10k, g20k);
}
