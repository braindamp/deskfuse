#pragma once
// Finite, explicit support for the live drift ASRC. Q32 identifies the
// source centre; input release is a DIFFERENT cursor, 63 frames behind it.
// The immutable table is constructed once on the lifecycle thread, before
// AAudio starts. No library-owned history, callback allocation or ratio FIFO.
#include <array>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <numbers>

namespace df_audio {
class StreamingSinc {
public:
    static constexpr uint32_t past = 63, future = 64, taps = 128, phases = 1024;
    static constexpr double cutoff = 0.46; // cycles/input frame; 22.08 kHz
    using Coefficients = std::array<float, taps>;
    struct Table {
        std::array<Coefficients, phases + 1> rows{};
        static double i0(double value) noexcept {
            double sum = 1, term = 1;
            for (uint32_t i = 1; i <= 48; ++i) {
                term *= value * value / (4 * static_cast<double>(i) * i);
                sum += term;
                if (term < sum * 1e-18) break;
            }
            return sum;
        }
        Table() noexcept {
            constexpr double beta = 12.0;
            const double normalization = i0(beta);
            for (uint32_t phase = 0; phase <= phases; ++phase) {
                const double fraction = static_cast<double>(phase) / phases;
                double sum = 0;
                for (uint32_t tap = 0; tap < taps; ++tap) {
                    const double distance = static_cast<double>(tap) - past - fraction;
                    const double angle = 2 * std::numbers::pi * cutoff * distance;
                    const double sinc = std::abs(angle) < 1e-12 ? 1 : std::sin(angle) / angle;
                    const double z = 2 * static_cast<double>(tap) / (taps - 1) - 1;
                    const double window = i0(beta * std::sqrt(std::max(0.0, 1-z*z))) / normalization;
                    const double value = 2 * cutoff * sinc * window;
                    rows[phase][tap] = static_cast<float>(value);
                    sum += value;
                }
                for (auto& value : rows[phase]) value = static_cast<float>(value / sum);
            }
        }
    };
    StreamingSinc() noexcept : table_(&shared_table()) {}
    // Caller supplies 63 past + centre + 64 future source samples. Missing
    // history at a NEW anchor is explicitly zero padded, never borrowed from
    // an old generation. Future support must exist; it is never extrapolated.
    template<class Read>
    float sample(uint32_t fraction, const Read& read) const noexcept {
        const uint32_t phase = fraction >> 22;
        const float blend = static_cast<float>(fraction & ((1u << 22) - 1)) /
            static_cast<float>(1u << 22);
        float result = 0;
        for (uint32_t tap = 0; tap < taps; ++tap) {
            const float a = table_->rows[phase][tap];
            const float b = table_->rows[phase + 1][tap];
            result += read(static_cast<int32_t>(tap) - static_cast<int32_t>(past)) *
                (a + (b - a) * blend);
        }
        return result;
    }
    std::array<float, 2> sample_stereo(uint32_t fraction, const int16_t* pcm,
                                     uint64_t capacity, uint64_t centre,
                                     uint64_t anchor) const noexcept {
        const uint32_t phase = fraction >> 22;
        const float blend = static_cast<float>(fraction & ((1u << 22) - 1)) /
            static_cast<float>(1u << 22);
        const uint64_t history = std::min<uint64_t>(centre-anchor,past);
        const uint32_t padding = past-static_cast<uint32_t>(history);
        uint64_t index = (centre-history) % capacity;
        std::array<float, 2> result{};
        const auto& row = table_->rows[phase];
        const auto& next = table_->rows[phase+1];
        for(uint32_t tap=padding;tap<taps;++tap) {
            const float coefficient=row[tap]+(next[tap]-row[tap])*blend;
            result[0]+=pcm[index*2]*coefficient;
            result[1]+=pcm[index*2+1]*coefficient;
            if(++index==capacity)index=0;
        }
        return result;
    }
private:
    static const Table& shared_table() noexcept {
        static const Table table{};
        return table;
    }
    const Table* table_;
};
} // namespace df_audio
