#pragma once
#include <cstddef>
#include <cstdint>
#include <limits>
#include <optional>

namespace df {

inline constexpr std::uint32_t kRate = 48'000;
inline constexpr std::uint16_t kChannels = 2;
inline constexpr std::uint16_t kMaxBlockFrames = 480;
inline constexpr std::uint64_t kNsPerSecond = 1'000'000'000ULL;

struct Generation {
    std::uint64_t source = 1;
    std::uint64_t play = 1;
    std::uint64_t connection = 1;
    friend constexpr bool operator==(const Generation&, const Generation&) = default;
};

struct FrameRange {
    std::uint64_t first = 0;
    std::uint32_t count = 0;
    [[nodiscard]] constexpr std::optional<std::uint64_t> end() const noexcept {
        if (count > std::numeric_limits<std::uint64_t>::max() - first) return std::nullopt;
        return first + count;
    }
    [[nodiscard]] constexpr bool valid() const noexcept { return count != 0 && end().has_value(); }
};

[[nodiscard]] constexpr bool overlaps(FrameRange a, FrameRange b) noexcept {
    if (!a.valid() || !b.valid()) return false;
    return a.first < *b.end() && b.first < *a.end();
}

[[nodiscard]] constexpr std::optional<std::uint64_t> bytes_for_frames(
    FrameRange range, std::uint32_t channels, std::uint32_t container_bytes) noexcept {
    if (!range.valid() || channels == 0 || container_bytes == 0) return std::nullopt;
    const std::uint64_t per_frame = static_cast<std::uint64_t>(channels) * container_bytes;
    if (per_frame > std::numeric_limits<std::uint64_t>::max() / range.count) return std::nullopt;
    return per_frame * range.count;
}

// Exact integer synthetic timeline at nominal 48 kHz: floor(n * 1e9 / 48000).
// Avoids 64-bit multiplication overflow and floating-point drift.
[[nodiscard]] constexpr std::optional<std::uint64_t> source_time_ns(std::uint64_t frame) noexcept {
    const std::uint64_t whole = frame / kRate;
    if (whole > std::numeric_limits<std::uint64_t>::max() / kNsPerSecond) return std::nullopt;
    const std::uint64_t base = whole * kNsPerSecond;
    const std::uint64_t fractional = ((frame % kRate) * kNsPerSecond) / kRate;
    if (fractional > std::numeric_limits<std::uint64_t>::max() - base) return std::nullopt;
    return base + fractional;
}

struct PcmBlock {
    Generation generation{};
    FrameRange range{};
    // Fixed storage: no allocation in publication or consumption.
    std::int16_t pcm[static_cast<std::size_t>(kMaxBlockFrames) * kChannels]{};
};

} // namespace df
