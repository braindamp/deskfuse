#pragma once
#include "deskfuse/frame.hpp"
#include <array>
#include <cstddef>
#include <cstdint>

namespace df {

enum class StoreResult : std::uint8_t {
    Accepted, Invalid, WrongGeneration, Late, Future, DuplicateOrOverlap, Full, CorruptPcm
};

class StrictSink final {
public:
    static constexpr std::size_t kBlocks = 20;
    static constexpr std::uint64_t kHorizonFrames = 4'800; // 100ms at 48kHz
    enum class Mode : std::uint8_t { StrictStop, AccountMissing };
    struct Stats {
        std::uint64_t received_frames = 0;
        std::uint64_t real_frames = 0;
        std::uint64_t missing_frames = 0;
        std::uint64_t rejected = 0;
        std::uint64_t corruption = 0;
        std::size_t high_water = 0;
        std::uint64_t rolling_hash = 14695981039346656037ULL;
    };

    explicit StrictSink(Generation generation = {}) noexcept : generation_(generation) {}
    void reset(Generation generation, std::uint64_t new_frontier = 0) noexcept;
    [[nodiscard]] StoreResult receive(const PcmBlock& block) noexcept;
    // StrictStop: refuses to advance across a hole. AccountMissing: commits
    // real or explicitly missing input and permanently prevents late rewrite.
    [[nodiscard]] bool commit_until(std::uint64_t exclusive_end, Mode mode) noexcept;
    [[nodiscard]] std::uint64_t sealed_frontier() const noexcept { return sealed_; }
    [[nodiscard]] const Stats& stats() const noexcept { return stats_; }
    [[nodiscard]] std::size_t occupancy() const noexcept { return occupancy_; }

private:
    struct Slot { bool occupied = false; PcmBlock block{}; };
    std::array<Slot, kBlocks> slots_{};
    Generation generation_{};
    std::uint64_t sealed_ = 0;
    Stats stats_{};
    std::size_t occupancy_ = 0;
    void release_committed() noexcept;
    [[nodiscard]] const PcmBlock* covering(std::uint64_t frame) const noexcept;
    void hash_sample(std::int16_t sample) noexcept;
};

} // namespace df
