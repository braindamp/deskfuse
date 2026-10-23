#pragma once
// CP-001/MP-001: actual allocation-free Android callback rendering core,
// compiled unchanged in native host tests. SPSC producer publishes PCM+tags
// with a release store. ONLY the output callback moves the consumer read tail.
// A single monotonically incremented invalidation ticket cannot be dropped
// even if many critical requests arrive before a callback.
#include "adaptive_clock.h"
#include "clock_qualification.h"
#include <array>
#include <atomic>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <limits>

namespace df_audio {

struct MediaIdentity {
    uint64_t source_frame = 0;
    uint32_t epoch = 0, stream = 0, format = 0, connection = 0;
    uint32_t playout = 0; // local callback generation, NOT stream identity
    uint8_t concealed = 0;
};
struct CoreSnapshot {
    uint64_t callback_seq = 0, committed_output = 0, rendered = 0, silence = 0;
    uint64_t released = 0, discarded = 0, concealed_outputs = 0;
    uint64_t underruns = 0, reprimes = 0, invalidations = 0;
    uint64_t last_source = 0, last_support_source = 0;
    uint64_t earliest_needed = 0, published_tail = 0;
    uint32_t fraction_q32 = 0, epoch = 0, stream = 0, format = 0;
    uint32_t connection = 0, playout = 0;
    int32_t drift_ppm = 0;
    uint32_t reserved = 0;
};
struct MailSlot {
    std::atomic<uint32_t> state{0}; // 0 empty, 1 writing, 2 ready, 3 reading
    CoreSnapshot value{};
};
class SnapshotMailbox {
public:
    // Callback-owned publisher; reader NEVER inspects a slot while writing.
    void publish(const CoreSnapshot& value) noexcept {
        for (auto& slot : slots_) {
            uint32_t expected = 0;
            if (!slot.state.compare_exchange_strong(expected, 1,
                    std::memory_order_acq_rel)) continue;
            slot.value = value;
            slot.state.store(2, std::memory_order_release);
            return;
        }
        dropped_.fetch_add(1, std::memory_order_relaxed);
    }
    bool pop(CoreSnapshot& result) noexcept {
        for (auto& slot : slots_) {
            uint32_t expected = 2;
            if (!slot.state.compare_exchange_strong(expected, 3,
                    std::memory_order_acq_rel)) continue;
            result = slot.value;
            slot.state.store(0, std::memory_order_release);
            return true;
        }
        return false;
    }
    uint64_t dropped() const noexcept {
        return dropped_.load(std::memory_order_relaxed);
    }
private:
    std::array<MailSlot, 8> slots_{};
    std::atomic<uint64_t> dropped_{0};
};

enum class Admission : int32_t {
    Accepted = 0, Overflow = 1, Invalid = -2,
    RecoverPending = -3, Discontinuity = -4, Stale = -5
};
struct RenderOptions { bool fade = true, linear = true; };

class RenderCore {
public:
    static constexpr uint64_t capacity = kRingFrames;
    static constexpr uint64_t prime = kPrimeFrames;
    static constexpr uint32_t fade_frames = 96;
    RenderCore() noexcept = default;
    RenderCore(const RenderCore&) = delete;
    RenderCore& operator=(const RenderCore&) = delete;

    // Producer-only operation. If invalidation is pending no new PCM can be
    // published. Request coalescing uses an atomic monotonic ticket (no
    // bounded channel overwrite or dropped critical invalidation).
    void invalidate() noexcept {
        recovery_ticket_.fetch_add(1, std::memory_order_acq_rel);
        producer_anchor_needed_ = true;
    }
    uint64_t requested_recoveries() const noexcept {
        return recovery_ticket_.load(std::memory_order_acquire);
    }
    uint64_t applied_recoveries() const noexcept {
        return recovery_applied_.load(std::memory_order_acquire);
    }

    Admission publish(const int16_t* samples, uint32_t frames,
                      MediaIdentity first) noexcept {
        if (!samples || frames != kBlockFrames || first.concealed > 1 ||
            first.source_frame > UINT64_MAX - frames)
            return Admission::Invalid;
        const uint64_t ticket = requested_recoveries();
        if (ticket != applied_recoveries()) return Admission::RecoverPending;
        const uint32_t local_generation =
            playout_generation_.load(std::memory_order_acquire);
        first.playout = local_generation;
        if (has_expected_ && !producer_anchor_needed_) {
            if (first.source_frame < expected_source_ &&
                same_context(first, last_published_)) return Admission::Stale;
            if (first.source_frame != expected_source_ ||
                !same_context(first, last_published_)) {
                invalidate();
                return Admission::Discontinuity;
            }
        }
        // After controlled reset accept ONLY a fresh explicit anchor >=
        // frontier for the same context, or a new authenticated context.
        if (has_expected_ && producer_anchor_needed_ &&
            same_context(first, last_published_) &&
            first.source_frame < expected_source_)
            return Admission::Stale;
        const uint64_t wr = write_.load(std::memory_order_relaxed);
        const uint64_t rd = read_.load(std::memory_order_acquire);
        if (wr < rd || wr - rd > capacity) { invalidate(); return Admission::Invalid; }
        if (capacity - (wr - rd) < frames) {
            overflow_.fetch_add(1, std::memory_order_relaxed);
            invalidate();
            return Admission::Overflow;
        }
        const uint64_t pos = wr % capacity;
        const uint64_t first_size = std::min<uint64_t>(frames, capacity - pos);
        std::memcpy(pcm_.data() + pos * kChannels, samples,
                    first_size * kChannels * sizeof(int16_t));
        if (frames > first_size)
            std::memcpy(pcm_.data(), samples + first_size * kChannels,
                        (frames - first_size) * kChannels * sizeof(int16_t));
        for (uint32_t i = 0; i < frames; ++i) {
            MediaIdentity tag = first;
            tag.source_frame += i;
            tags_[(wr + i) % capacity] = tag;
        }
        // Callback may trigger autonomous underrun while producer is writing.
        // Don't publish the partial reservation under a retired ticket.
        if (recovery_ticket_.load(std::memory_order_acquire) != ticket)
            return Admission::RecoverPending;
        write_.store(wr + frames, std::memory_order_release);
        published_.fetch_add(frames, std::memory_order_relaxed);
        expected_source_ = first.source_frame + frames;
        last_published_ = first;
        has_expected_ = true;
        producer_anchor_needed_ = false;
        return Admission::Accepted;
    }

    void render(int16_t* out, uint32_t count, RenderOptions options) noexcept {
        if (!out || !count) return;
        std::memset(out, 0, size_t(count) * kChannels * sizeof(int16_t));
        const uint64_t start = read_.load(std::memory_order_relaxed);
        uint64_t rd = start;
        const uint64_t wr = write_.load(std::memory_order_acquire);
        if (wr < rd || wr - rd > capacity) {
            invalidations_++;
            request_from_callback();
            rd = wr;
        }
        const uint64_t target_ticket =
            recovery_ticket_.load(std::memory_order_acquire);
        if (target_ticket != recovery_applied_.load(std::memory_order_relaxed)) {
            discarded_ += wr - rd;
            rd = wr;
            reset_output();
            recovery_applied_.store(target_ticket, std::memory_order_release);
            ++invalidations_;
        }
        uint32_t emitted = 0;
        const uint64_t ready = wr - rd;
        if (!primed_) {
            if (ready >= prime) {
                primed_ = true;
                if (ever_primed_) ++reprimes_;
                ever_primed_ = true;
                fade_remaining_ = fade_frames;
                follower_.reset();
                fraction_q32_ = 0;
            }
        }
        if (primed_) {
            double ppm = 0;
            uint64_t step = kOneQ32;
            if (options.linear) {
                ppm = follower_.update(count,
                    static_cast<int64_t>(ready),
                    static_cast<int64_t>(kTargetFrames));
                step += static_cast<int64_t>(
                    static_cast<double>(kOneQ32) * ppm / 1'000'000.0);
            }
            drift_ppm_ = options.linear ? static_cast<int32_t>(ppm) : 0;
            while (emitted < count && wr - rd >= (options.linear ? 2u : 1u)) {
                const auto& a = tags_[rd % capacity];
                const uint64_t position = fraction_q32_;
                const MediaIdentity* b = options.linear ?
                    &tags_[(rd + 1) % capacity] : nullptr;
                if (b && !adjacent(a, *b)) {
                    // NEVER interpolate across a missing interval or changed
                    // epoch/format/connection/playout identity.
                    request_from_callback();
                    break;
                }
                if (last_commit_valid_ &&
                    same_context(a, last_commit_) &&
                    (a.source_frame < last_commit_.source_frame ||
                     (a.source_frame == last_commit_.source_frame &&
                      position < last_fraction_))) {
                    request_from_callback();
                    break;
                }
                for (uint32_t ch = 0; ch < kChannels; ++ch) {
                    const int16_t x = pcm_[(rd % capacity) * kChannels + ch];
                    int16_t sample = x;
                    if (b) {
                        const int16_t y =
                            pcm_[((rd + 1) % capacity) * kChannels + ch];
                        sample = DriftFollower::sample(
                            x, y, static_cast<uint32_t>(position));
                    }
                    out[emitted * kChannels + ch] = sample;
                }
                if (a.concealed ||
                    (b && position != 0 && b->concealed))
                    ++concealed_outputs_;
                last_commit_ = a;
                last_support_ = (b && position != 0) ? *b : a;
                last_fraction_ = static_cast<uint32_t>(position);
                last_commit_valid_ = true;
                ++emitted;
                ++committed_output_;
                const uint64_t next = fraction_q32_ + step;
                rd += next >> 32;
                fraction_q32_ = static_cast<uint32_t>(next);
            }
            if (options.fade && emitted && fade_remaining_) {
                const uint32_t n = std::min(emitted, fade_remaining_);
                const uint32_t begin = fade_frames - fade_remaining_;
                for (uint32_t i = 0; i < n; ++i)
                    for (uint32_t ch = 0; ch < kChannels; ++ch) {
                        auto& sample = out[i * kChannels + ch];
                        sample = static_cast<int16_t>(
                            static_cast<int32_t>(sample) * (begin + i + 1) /
                            fade_frames);
                    }
                fade_remaining_ -= n;
            }
            if (emitted < count) {
                // AAudio will commit the emitted prefix and defined silence.
                // Explicitly discard unplayable lookahead/old generation and
                // require a new producer anchor after callback-owned reset.
                if (options.fade && emitted) {
                    const uint32_t tail = std::min(emitted, fade_frames);
                    for (uint32_t i = 0; i < tail; ++i)
                        for (uint32_t ch = 0; ch < kChannels; ++ch) {
                            auto& sample =
                                out[(emitted - tail + i) * kChannels + ch];
                            sample = static_cast<int16_t>(
                                static_cast<int32_t>(sample) * (tail - i - 1) /
                                tail);
                        }
                }
                ++underruns_;
                request_from_callback();
            }
        }
        // release only AFTER all two-tap support reads for committed output.
        if (rd > wr) rd = wr;
        released_ += rd - start;
        read_.store(rd, std::memory_order_release);
        rendered_ += emitted;
        silence_ += count - emitted;
        committed_output_ += count - emitted;
        ++callbacks_;
        CoreSnapshot record{};
        record.callback_seq = callbacks_;
        record.committed_output = committed_output_;
        record.rendered = rendered_;
        record.silence = silence_;
        record.released = released_;
        record.discarded = discarded_;
        record.concealed_outputs = concealed_outputs_;
        record.underruns = underruns_;
        record.reprimes = reprimes_;
        record.invalidations = invalidations_;
        record.last_source = last_commit_.source_frame;
        record.last_support_source = last_support_.source_frame;
        record.earliest_needed = rd;
        record.published_tail = wr;
        record.fraction_q32 = last_fraction_;
        record.epoch = last_commit_.epoch;
        record.stream = last_commit_.stream;
        record.format = last_commit_.format;
        record.connection = last_commit_.connection;
        record.playout = last_commit_.playout;
        record.drift_ppm = drift_ppm_;
        snapshots_.publish(record);
    }
    bool pop_snapshot(CoreSnapshot& record) noexcept {
        return snapshots_.pop(record);
    }
    uint64_t dropped_snapshots() const noexcept { return snapshots_.dropped(); }
    uint64_t published() const noexcept { return published_.load(std::memory_order_relaxed); }
    uint64_t overflow() const noexcept { return overflow_.load(std::memory_order_relaxed); }
    uint64_t read() const noexcept { return read_.load(std::memory_order_acquire); }
    uint64_t write() const noexcept { return write_.load(std::memory_order_acquire); }

private:
    static bool same_context(const MediaIdentity& a,
                             const MediaIdentity& b) noexcept {
        return a.epoch == b.epoch && a.stream == b.stream &&
            a.format == b.format && a.connection == b.connection &&
            a.playout == b.playout;
    }
    static bool adjacent(const MediaIdentity& a,
                         const MediaIdentity& b) noexcept {
        return a.source_frame != UINT64_MAX &&
            b.source_frame == a.source_frame + 1 &&
            same_context(a, b);
    }
    void request_from_callback() noexcept {
        recovery_ticket_.fetch_add(1, std::memory_order_acq_rel);
    }
    void reset_output() noexcept {
        primed_ = false;
        fade_remaining_ = fade_frames;
        follower_.reset();
        fraction_q32_ = 0;
        drift_ppm_ = 0;
        playout_generation_.fetch_add(1, std::memory_order_acq_rel);
        last_commit_valid_ = false;
    }
    alignas(64) std::array<int16_t, capacity * kChannels> pcm_{};
    std::array<MediaIdentity, capacity> tags_{};
    std::atomic<uint64_t> read_{0}, write_{0};
    std::atomic<uint64_t> recovery_ticket_{0}, recovery_applied_{0};
    std::atomic<uint32_t> playout_generation_{1};
    std::atomic<uint64_t> published_{0}, overflow_{0};
    // Producer-only fields:
    bool has_expected_ = false, producer_anchor_needed_ = true;
    uint64_t expected_source_ = 0;
    MediaIdentity last_published_{};
    // Callback-only:
    bool primed_ = false, ever_primed_ = false, last_commit_valid_ = false;
    uint32_t fade_remaining_ = fade_frames, fraction_q32_ = 0, last_fraction_ = 0;
    MediaIdentity last_commit_{}, last_support_{};
    TimeBasedFollower follower_{};
    uint64_t callbacks_ = 0, committed_output_ = 0, rendered_ = 0;
    uint64_t silence_ = 0, released_ = 0, discarded_ = 0;
    uint64_t concealed_outputs_ = 0, underruns_ = 0, reprimes_ = 0;
    uint64_t invalidations_ = 0;
    int32_t drift_ppm_ = 0;
    SnapshotMailbox snapshots_{};
};
} // namespace df_audio
