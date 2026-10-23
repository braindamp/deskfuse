#pragma once
// MP-001 render source-identity accounting model. The caller owns all SPSC
// memory barriers; this class tests source/commit/release policy separately.
// Each slot represents ONE physical source frame, not one QUIC sequence.
// Publication is separate from committing AAudio output and releasing source
// storage. The last submitted output frame cannot be rewritten retroactively.
#include "adaptive_clock.h"
#include <array>
#include <cstdint>
#include <limits>

namespace df_audio {
enum class FrameKind : uint8_t { Captured = 0, Concealed = 1 };
struct SourceIdentity {
    uint64_t source_frame = 0;
    uint32_t epoch = 0;
    uint32_t generation = 0;
    FrameKind kind = FrameKind::Captured;
};
struct LedgerCounters {
    uint64_t published_source_frames = 0;
    uint64_t concealed_source_frames = 0;
    uint64_t source_gap_frames = 0;
    uint64_t committed_aaudio_output_frames = 0;
    uint64_t committed_concealment_frames = 0;
    uint64_t output_silence_frames = 0;
    uint64_t released_source_frames = 0;
    uint64_t discarded_on_rebuffer_frames = 0;
    uint64_t last_committed_source_frame = 0;
    uint32_t last_committed_fraction_q32 = 0;
    uint32_t last_epoch = 0;
    uint32_t last_generation = 0;
};
class SourceLedger {
public:
    // Slot identity is published BEFORE PCM write_pos release, on producer
    // thread. Caller must not permit an overlapping reservation.
    bool publish(uint64_t write_pos, uint64_t read_pos,
                 uint64_t first_source, uint32_t frames,
                 uint32_t epoch, uint32_t generation,
                 FrameKind kind) noexcept {
        if (!frames || frames > kBlockFrames || write_pos < read_pos ||
            write_pos - read_pos + frames > kRingFrames ||
            first_source > std::numeric_limits<uint64_t>::max() - frames)
            return false;
        if (has_last_published_ && epoch == previous_epoch_ &&
            generation == previous_generation_) {
            if (first_source < next_expected_source_) return false;
            counters.source_gap_frames += first_source - next_expected_source_;
        }
        for (uint32_t frame = 0; frame < frames; ++frame) {
            slots_[(write_pos + frame) % kRingFrames] =
                {first_source + frame, epoch, generation, kind};
        }
        counters.published_source_frames += frames;
        if (kind == FrameKind::Concealed)
            counters.concealed_source_frames += frames;
        previous_epoch_ = epoch;
        previous_generation_ = generation;
        next_expected_source_ = first_source + frames;
        has_last_published_ = true;
        return true;
    }

    const SourceIdentity& tag(uint64_t slot) const noexcept {
        return slots_[slot % kRingFrames];
    }
    // Output callback has COMMITTED one frame to AAudio's returned buffer.
    // Re-sampled output may repeat source_frame with advancing fractional
    // phase. Backward source order within a generation is never permitted.
    bool commit(uint64_t slot, uint32_t fraction_q32) noexcept {
        const auto& id = tag(slot);
        if (has_last_committed_ && id.epoch == counters.last_epoch &&
            id.generation == counters.last_generation) {
            if (id.source_frame < counters.last_committed_source_frame ||
                (id.source_frame == counters.last_committed_source_frame &&
                 fraction_q32 < counters.last_committed_fraction_q32))
                return false;
        }
        counters.committed_aaudio_output_frames += 1;
        if (id.kind == FrameKind::Concealed)
            counters.committed_concealment_frames += 1;
        counters.last_committed_source_frame = id.source_frame;
        counters.last_committed_fraction_q32 = fraction_q32;
        counters.last_epoch = id.epoch;
        counters.last_generation = id.generation;
        has_last_committed_ = true;
        return true;
    }
    void output_silence(uint64_t frames) noexcept {
        counters.output_silence_frames += frames;
        counters.committed_aaudio_output_frames += frames;
    }
    bool release(uint64_t from, uint64_t to, uint64_t write_pos,
                 bool rebuffer = false) noexcept {
        if (to < from || to > write_pos) return false;
        counters.released_source_frames += to - from;
        if (rebuffer) counters.discarded_on_rebuffer_frames += to - from;
        return true;
    }
    LedgerCounters counters{};
private:
    std::array<SourceIdentity, kRingFrames> slots_{};
    uint64_t next_expected_source_ = 0;
    uint32_t previous_epoch_ = 0;
    uint32_t previous_generation_ = 0;
    bool has_last_published_ = false;
    bool has_last_committed_ = false;
};
} // namespace df_audio
