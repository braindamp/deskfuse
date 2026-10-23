#include "deskfuse/audio_wire.hpp"
#include <array>
#include <cstdint>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

#define REQUIRE(c) do { if (!(c)) throw std::runtime_error(std::string("failed: ") + #c); } while(false)
using namespace df::wire;

static std::vector<std::uint8_t> packet(Header h = {}) {
    if (h.frame_count == 0) h.frame_count = 240;
    if (h.payload_bytes == 0) h.payload_bytes = h.frame_count * 4U;
    if (h.media_epoch == 0) h.media_epoch = 1;
    if (h.source_stream_id == 0) h.source_stream_id = 1;
    if (h.format_config_id == 0) h.format_config_id = 1;
    std::array<std::uint8_t, kHeaderBytes> b{};
    encode(h, b);
    std::vector<std::uint8_t> result(b.begin(), b.end());
    result.insert(result.end(), h.payload_bytes, 0x42);
    return result;
}

int main() {
    try {
        Configuration cfg{};
        Parsed out{};
        Header h{}; h.frame_count=240; h.media_epoch=1; h.source_stream_id=1;
        h.format_config_id=1; h.packet_sequence=0x12345678;
        h.first_source_frame=0x0102030405060708ULL; h.payload_bytes=960;
        h.clock_anchor_id=0xAABBCCDD;
        const auto good=packet(h);
        REQUIRE(good.size() == 1000);
        REQUIRE(good[20] == 0x12 && good[21] == 0x34 && good[22] == 0x56 && good[23] == 0x78);
        REQUIRE(good[24] == 0x01 && good[31] == 0x08);
        REQUIRE(parse(good, cfg, out) == Result::Ok);
        REQUIRE(out.header.first_source_frame == h.first_source_frame);
        REQUIRE(out.header.clock_anchor_id == h.clock_anchor_id);
        REQUIRE(out.pcm_bytes.data() == good.data() + kHeaderBytes);
        REQUIRE(out.pcm_bytes.size() == 960);
        auto save=out;
        REQUIRE(parse(std::span<const std::uint8_t>(good.data(),39),cfg,out) == Result::TooShort);
        REQUIRE(out.header.packet_sequence == save.header.packet_sequence);
        for (std::size_t n=0;n<40;++n)
            REQUIRE(parse(std::span<const std::uint8_t>(good.data(),n),cfg,out) == Result::TooShort);
        auto b=good; b[0]=2; REQUIRE(parse(b,cfg,out)==Result::UnsupportedVersion);
        b=good; b[1]=2; REQUIRE(parse(b,cfg,out)==Result::UnsupportedKind);
        b=good; b[3]=1; REQUIRE(parse(b,cfg,out)==Result::UnsupportedFlags);
        b=good; b[5]=39; REQUIRE(parse(b,cfg,out)==Result::BadHeaderLength);
        b=good; b[6]=0; b[7]=0; REQUIRE(parse(b,cfg,out)==Result::InvalidFrameRange);
        b=good; b[6]=1; b[7]=225; REQUIRE(parse(b,cfg,out)==Result::InvalidFrameRange);
        b=good; b[11]=2; REQUIRE(parse(b,cfg,out)==Result::WrongGeneration);
        b=good; b[15]=2; REQUIRE(parse(b,cfg,out)==Result::WrongStream);
        b=good; b[19]=2; REQUIRE(parse(b,cfg,out)==Result::WrongFormat);
        b=good; b[35]=1; REQUIRE(parse(b,cfg,out)==Result::PayloadLength);
        b=good; b.pop_back(); REQUIRE(parse(b,cfg,out)==Result::PayloadLength);
        cfg.datagram_limit_bytes=999; REQUIRE(parse(good,cfg,out)==Result::TooLarge);
        cfg.datagram_limit_bytes=1200;
        cfg.channels=1; REQUIRE(parse(good,cfg,out)==Result::PayloadLength);
        cfg.channels=2; cfg.container_bytes=0; REQUIRE(parse(good,cfg,out)==Result::BadConfiguration);
        cfg.container_bytes=2;
        h.first_source_frame=std::numeric_limits<std::uint64_t>::max()-100;
        REQUIRE(parse(packet(h),cfg,out)==Result::InvalidFrameRange);
        // Mutating raw payload is permitted here because authentication is an upstream boundary.
        // This is a parser-safety test, NOT an authenticated-corruption-detection test.
        std::uint64_t classifications=0;
        for (std::size_t i=0;i<good.size();++i) {
            auto mutation=good;
            mutation[i] ^= 0xFF;
            Parsed result{};
            const auto status=parse(mutation,cfg,result);
            classifications+=static_cast<std::uint8_t>(status);
        }
        REQUIRE(classifications > 0);
        std::cout << "PASS wire_header_and_adversarial_lengths; mutations=" << good.size() << '\n';
        return 0;
    } catch(const std::exception& e) {
        std::cerr << "FAIL " << e.what() << '\n';
        return 1;
    }
}
