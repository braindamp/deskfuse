#pragma once
// M2a wire format: 40 big-endian bytes. The enclosing authenticated QUIC
// connection verifies peer identity and packet integrity BEFORE this parser.
// No secondary encryption, CRC or speculative allocation is added here.
#include "deskfuse/frame.hpp"
#include <cstddef>
#include <cstdint>
#include <span>

namespace df::wire {
inline constexpr std::size_t kHeaderBytes = 40;
inline constexpr std::uint8_t kVersion = 1;
inline constexpr std::uint8_t kAudioKind = 1;

struct Header {
    std::uint8_t version = kVersion;
    std::uint8_t kind = kAudioKind;
    std::uint16_t flags = 0;
    std::uint16_t header_bytes = static_cast<std::uint16_t>(kHeaderBytes);
    std::uint16_t frame_count = 0;
    std::uint32_t media_epoch = 0;
    std::uint32_t source_stream_id = 0;
    std::uint32_t format_config_id = 0;
    std::uint32_t packet_sequence = 0;
    std::uint64_t first_source_frame = 0;
    std::uint32_t payload_bytes = 0;
    std::uint32_t clock_anchor_id = 0;
};

struct Configuration {
    std::uint32_t media_epoch = 1;
    std::uint32_t source_stream_id = 1;
    std::uint32_t format_config_id = 1;
    std::uint16_t channels = 2;
    std::uint16_t container_bytes = 2;
    std::uint16_t maximum_frames = 480;
    std::size_t datagram_limit_bytes = 1200;
};

enum class Result : std::uint8_t {
    Ok, TooShort, TooLarge, UnsupportedVersion, UnsupportedKind,
    UnsupportedFlags, BadHeaderLength, InvalidFrameRange, WrongGeneration,
    WrongStream, WrongFormat, PayloadLength, BadConfiguration
};

// A borrowed view: the ingress owner must copy into a fixed owned PCM block
// before releasing the authenticated QUIC receive buffer.
struct Parsed {
    Header header{};
    std::span<const std::uint8_t> pcm_bytes{};
};

[[nodiscard]] inline std::uint16_t read16(const std::uint8_t* b) noexcept {
    return static_cast<std::uint16_t>((static_cast<std::uint16_t>(b[0]) << 8U) | b[1]);
}
[[nodiscard]] inline std::uint32_t read32(const std::uint8_t* b) noexcept {
    return (static_cast<std::uint32_t>(b[0]) << 24U) |
           (static_cast<std::uint32_t>(b[1]) << 16U) |
           (static_cast<std::uint32_t>(b[2]) << 8U) |
           static_cast<std::uint32_t>(b[3]);
}
[[nodiscard]] inline std::uint64_t read64(const std::uint8_t* b) noexcept {
    return (static_cast<std::uint64_t>(read32(b)) << 32U) | read32(b + 4);
}
inline void write16(std::uint8_t* b, std::uint16_t n) noexcept {
    b[0] = static_cast<std::uint8_t>(n >> 8U);
    b[1] = static_cast<std::uint8_t>(n);
}
inline void write32(std::uint8_t* b, std::uint32_t n) noexcept {
    b[0] = static_cast<std::uint8_t>(n >> 24U);
    b[1] = static_cast<std::uint8_t>(n >> 16U);
    b[2] = static_cast<std::uint8_t>(n >> 8U);
    b[3] = static_cast<std::uint8_t>(n);
}
inline void write64(std::uint8_t* b, std::uint64_t n) noexcept {
    write32(b, static_cast<std::uint32_t>(n >> 32U));
    write32(b + 4, static_cast<std::uint32_t>(n));
}

// Header-only serialization. Caller owns packet preparation and crypto policy.
inline void encode(const Header& h, std::span<std::uint8_t, kHeaderBytes> b) noexcept {
    b[0] = h.version; b[1] = h.kind;
    write16(b.data() + 2, h.flags);
    write16(b.data() + 4, h.header_bytes);
    write16(b.data() + 6, h.frame_count);
    write32(b.data() + 8, h.media_epoch);
    write32(b.data() + 12, h.source_stream_id);
    write32(b.data() + 16, h.format_config_id);
    write32(b.data() + 20, h.packet_sequence);
    write64(b.data() + 24, h.first_source_frame);
    write32(b.data() + 32, h.payload_bytes);
    write32(b.data() + 36, h.clock_anchor_id);
}

[[nodiscard]] inline Result parse(std::span<const std::uint8_t> data,
                                   const Configuration& cfg, Parsed& out) noexcept {
    // Invalid input does not mutate an existing parsed record. No heap usage.
    if (cfg.channels == 0 || cfg.container_bytes == 0 ||
        cfg.maximum_frames == 0 || cfg.datagram_limit_bytes < kHeaderBytes)
        return Result::BadConfiguration;
    if (data.size() < kHeaderBytes) return Result::TooShort;
    if (data.size() > cfg.datagram_limit_bytes) return Result::TooLarge;

    const auto* b = data.data();
    Header h{};
    h.version = b[0]; h.kind = b[1]; h.flags = read16(b + 2);
    h.header_bytes = read16(b + 4); h.frame_count = read16(b + 6);
    h.media_epoch = read32(b + 8); h.source_stream_id = read32(b + 12);
    h.format_config_id = read32(b + 16); h.packet_sequence = read32(b + 20);
    h.first_source_frame = read64(b + 24); h.payload_bytes = read32(b + 32);
    h.clock_anchor_id = read32(b + 36);

    if (h.version != kVersion) return Result::UnsupportedVersion;
    if (h.kind != kAudioKind) return Result::UnsupportedKind;
    if (h.flags != 0) return Result::UnsupportedFlags;
    if (h.header_bytes != kHeaderBytes) return Result::BadHeaderLength;
    if (h.frame_count == 0 || h.frame_count > cfg.maximum_frames ||
        !FrameRange{h.first_source_frame, h.frame_count}.valid())
        return Result::InvalidFrameRange;
    if (h.media_epoch != cfg.media_epoch) return Result::WrongGeneration;
    if (h.source_stream_id != cfg.source_stream_id) return Result::WrongStream;
    if (h.format_config_id != cfg.format_config_id) return Result::WrongFormat;
    const auto expected = bytes_for_frames({h.first_source_frame, h.frame_count},
                                           cfg.channels, cfg.container_bytes);
    if (!expected || *expected != h.payload_bytes ||
        *expected != data.size() - kHeaderBytes)
        return Result::PayloadLength;
    out.header = h;
    out.pcm_bytes = data.subspan(kHeaderBytes);
    return Result::Ok;
}
} // namespace df::wire
