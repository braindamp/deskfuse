#pragma once
// CP-001/MP-001: actual allocation-free Android callback rendering core,
// compiled unchanged in native host tests. SPSC producer publishes PCM+tags
// with a release store. ONLY the output callback moves the consumer read tail.
// A single monotonically incremented invalidation ticket cannot be dropped
// even if many critical requests arrive before a callback.
#include "adaptive_clock.h"
#include "clock_qualification.h"
#include "streaming_sinc.h"
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
    // SPSC FIFOs cannot scan for "any ready" slot: slot reuse could return
    // newer callback N+1 before older callback N. Both positions are
    // thread-owned and advance only after a successfully published record.
    void publish(const CoreSnapshot& value) noexcept {
        auto& slot = slots_[next_write_ % slots_.size()];
        if (slot.state.load(std::memory_order_acquire) != 0) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        slot.value = value;
        slot.state.store(2, std::memory_order_release);
        ++next_write_;
    }
    bool pop(CoreSnapshot& result) noexcept {
        auto& slot = slots_[next_read_ % slots_.size()];
        if (slot.state.load(std::memory_order_acquire) != 2) return false;
        result = slot.value;
        slot.state.store(0, std::memory_order_release);
        ++next_read_;
        return true;
    }
    uint64_t dropped() const noexcept {
        return dropped_.load(std::memory_order_relaxed);
    }
private:
    std::array<MailSlot, 8> slots_{};
    uint64_t next_write_ = 0; // callback-owned
    uint64_t next_read_ = 0;  // off-callback telemetry-consumer-owned
    std::atomic<uint64_t> dropped_{0};
};

enum class Admission : int32_t {
    Accepted = 0, Overflow = 1, Invalid = -2,
    RecoverPending = -3, Discontinuity = -4, Stale = -5
};
struct RenderOptions { bool fade = true, linear = true; };
// "linear" is the historical option name/ABI bit. It now selects the
// qualified finite-support sinc ASRC; false remains exact identity bypass.
struct PlayoutState {
    uint64_t queued_playable_frames = 0, current_source_frame = 0;
    uint64_t requested_recovery = 0, applied_recovery = 0;
    uint32_t primed = 0, last_callback_frames = 0, max_callback_frames = 0;
    uint32_t playout_generation = 0;
    int32_t stream_error = 0;
    uint32_t fraction_q32 = 0, source_valid = 0;
    int32_t xrun_count = 0; // off-callback AAudio observation, may be an error
    uint64_t clipped_samples = 0; // saturating PCM16 conversion, both channels
};
static_assert(sizeof(PlayoutState) == 72);
#ifdef DESKFUSE_TEST_PROBE
enum class RenderProbe { BeforePublicationCommit, BeforeRecoveryTailRelease };
#endif

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
        return publish_impl(samples, frames, first, false, 0);
    }
    // The authenticated receiver authorizes exactly one applied recovery.
    // Publication ownership keeps that authorization valid through commit.
    Admission publish_guarded(const int16_t* samples, uint32_t frames,
                              MediaIdentity first,
                              uint64_t expected_applied) noexcept {
        return publish_impl(samples, frames, first, true, expected_applied);
    }

private:
    static constexpr uint32_t kIdle = 0, kPublishing = 1, kRecovering = 2;
    struct PublicationRelease {
        std::atomic<uint32_t>& state;
        ~PublicationRelease() noexcept { state.store(kIdle, std::memory_order_release); }
    };
    Admission publish_impl(const int16_t* samples, uint32_t frames,
                           MediaIdentity first, bool guarded,
                           uint64_t expected_applied) noexcept {
        if (!samples || frames != kBlockFrames || first.concealed > 1 ||
            first.source_frame > UINT64_MAX - frames)
            return Admission::Invalid;
        uint32_t idle = kIdle;
        if (!publication_state_.compare_exchange_strong(idle, kPublishing,
                std::memory_order_acq_rel, std::memory_order_acquire))
            return Admission::RecoverPending;
        const PublicationRelease release{publication_state_};
        const uint64_t ticket = requested_recoveries();
        const uint64_t applied = applied_recoveries();
        if (ticket != applied || (guarded && expected_applied != applied))
            return Admission::RecoverPending;
        if (applied != last_observed_recovery_) {
            // Callback applied an autonomous underrun/format invalidation.
            // Producer can resume ONLY from a forward source anchor.
            last_observed_recovery_ = applied;
            producer_anchor_needed_ = true;
        }
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
            same_source_timeline(first, last_published_) &&
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
        // A callback can REQUEST recovery during this reservation, but cannot
        // APPLY it until publication ownership is released. Even if preempted
        // after this check, these tags cannot outlive a completed reset: the
        // callback defers recovery without waiting, then discards this commit.
        if (recovery_ticket_.load(std::memory_order_acquire) != ticket)
            return Admission::RecoverPending;
#ifdef DESKFUSE_TEST_PROBE
        probe(RenderProbe::BeforePublicationCommit);
#endif
        write_.store(wr + frames, std::memory_order_release);
        published_.fetch_add(frames, std::memory_order_relaxed);
        expected_source_ = first.source_frame + frames;
        last_published_ = first;
        has_expected_ = true;
        producer_anchor_needed_ = false;
        return Admission::Accepted;
    }

public:
    void render(int16_t* out, uint32_t count, RenderOptions options) noexcept {
        if (!out || !count) return;
        playout_sequence_.fetch_add(1, std::memory_order_acq_rel);
        const uint32_t required_future = options.linear ?
            std::max(StreamingSinc::future, options.fade ? fade_frames : 0u) : 0u;
        std::memset(out, 0, size_t(count) * kChannels * sizeof(int16_t));
        const uint64_t start = read_.load(std::memory_order_relaxed);
        uint64_t rd = render_cursor_;
        uint64_t wr = write_.load(std::memory_order_acquire);
        if (wr < rd || wr - rd > capacity) {
            invalidations_++;
            request_from_callback();
            rd = wr;
        }
        const uint64_t target_ticket =
            recovery_ticket_.load(std::memory_order_acquire);
        bool recovery_owned = false, recovery_deferred = false;
        if (target_ticket != recovery_applied_.load(std::memory_order_relaxed)) {
            uint32_t idle = kIdle;
            recovery_owned = publication_state_.compare_exchange_strong(
                idle, kRecovering, std::memory_order_acq_rel,
                std::memory_order_acquire);
            if (recovery_owned) {
                // Refresh AFTER claiming ownership: a producer may have
                // finished since the initial write snapshot. No reservation
                // can commit behind this reset while we own kRecovering.
                wr = write_.load(std::memory_order_acquire);
                discarded_ += wr - rd;
                rd = wr;
                reset_output(wr);
                ++invalidations_;
            } else {
                // Never wait for an unscheduled producer in the callback.
                // Commit defined silence until its reservation is resolved.
                recovery_deferred = true;
            }
        }
        uint32_t emitted = 0;
        const uint64_t ready = wr - rd;
        if (!primed_ && !recovery_deferred) {
            if (ready >= prime) {
                primed_ = true;
                if (ever_primed_) ++reprimes_;
                ever_primed_ = true;
                fade_remaining_ = fade_frames;
                follower_.reset();
                fraction_q32_ = 0;
            }
        }
        if (primed_ && !recovery_deferred) {
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
            // Validate the complete immutable read span ONCE, not 128 tags
            // per output sample. The SPSC read tail remains held throughout.
            const uint64_t support_begin = options.linear ?
                rd-std::min<uint64_t>(rd-history_anchor_,StreamingSinc::past) : rd;
            const uint64_t maximum_advance = (fraction_q32_ + uint64_t(count)*step) >> 32;
            const uint64_t support_end = std::min(wr,rd+maximum_advance+required_future+1);
            bool support_valid = true;
            conceal_prefix_[0] = 0;
            if (rd < wr) {
                const auto first = tags_[support_begin % capacity];
                const uint32_t generation = playout_generation_.load(std::memory_order_relaxed);
                for (uint64_t index=support_begin;index<support_end;++index) {
                    const auto& tag = tags_[index % capacity];
                    if (tag.playout != generation || !same_context(first,tag) ||
                        tag.source_frame != first.source_frame+(index-support_begin))
                        support_valid = false;
                    conceal_prefix_[index-support_begin+1] = static_cast<uint16_t>(
                        conceal_prefix_[index-support_begin] + (tag.concealed ? 1u : 0u));
                }
            }
            auto find_concealment = [&](uint64_t from) noexcept {
                for (uint64_t index = from; index < wr; ++index)
                    if (tags_[index % capacity].concealed) return index;
                return wr;
            };
            uint64_t next_concealed = find_concealment(rd);
            while (emitted < count && wr - rd >= required_future + 1u) {
                const auto& a = tags_[rd % capacity];
                const uint64_t position = fraction_q32_;
                const MediaIdentity* b = options.linear ?
                    &tags_[(rd + StreamingSinc::future) % capacity] : nullptr;
                bool support_concealed = a.concealed != 0;
                if (options.linear) {
                    const uint64_t first = rd-std::min<uint64_t>(rd-history_anchor_,StreamingSinc::past);
                    const uint64_t end = rd+StreamingSinc::future+1;
                    support_concealed = rd-history_anchor_ < StreamingSinc::past ||
                        conceal_prefix_[end-support_begin] != conceal_prefix_[first-support_begin];
                }
                if (a.playout != playout_generation_.load(std::memory_order_relaxed) ||
                    !support_valid) {
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
                if (options.fade && last_kind_valid_ && (a.concealed != 0) != last_kind_concealed_) {
                    if (!a.concealed) {
                        fade_remaining_ = fade_frames;
                        next_concealed = find_concealment(rd);
                    }
                }
                last_kind_valid_ = true;
                last_kind_concealed_ = a.concealed != 0;
                std::array<float, kChannels> filtered{};
                if (b) filtered = sinc_.sample_stereo(static_cast<uint32_t>(position),
                    pcm_.data(),capacity,rd,history_anchor_);
                for (uint32_t ch = 0; ch < kChannels; ++ch) {
                    const int16_t x = pcm_[(rd % capacity) * kChannels + ch];
                    int16_t sample = x;
                    if (b) {
                        // Bandlimited reconstruction can overshoot arbitrary
                        // full-scale PCM. Explicitly count and saturate each
                        // affected channel sample; never wrap or hide gain.
                        const long rounded = std::lround(filtered[ch]);
                        if (rounded < -32768 || rounded > 32767) ++clipped_samples_;
                        sample = static_cast<int16_t>(std::clamp(rounded,-32768L,32767L));
                    }
                    if (options.fade && a.concealed) {
                        sample = 0; // explicit missing interval, no cached old support
                    } else if (options.fade) {
                        if (fade_remaining_)
                            sample = static_cast<int16_t>(static_cast<int64_t>(sample) *
                                (fade_frames - fade_remaining_ + 1) / fade_frames);
                        if (next_concealed < wr && next_concealed > rd &&
                            next_concealed - rd <= fade_frames)
                            sample = static_cast<int16_t>(static_cast<int64_t>(sample) *
                                static_cast<int64_t>(next_concealed - rd - 1) / fade_frames);
                    }
                    out[emitted * kChannels + ch] = sample;
                }
                if (options.fade && !a.concealed && fade_remaining_) --fade_remaining_;
                if (support_concealed)
                    ++concealed_outputs_;
                last_commit_ = a;
                last_support_ = b ? *b : a;
                last_fraction_ = static_cast<uint32_t>(position);
                last_commit_valid_ = true;
                ++emitted;
                ++committed_output_;
                const uint64_t next = fraction_q32_ + step;
                rd += next >> 32;
                fraction_q32_ = static_cast<uint32_t>(next);
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
                                static_cast<int64_t>(sample) * static_cast<int64_t>(tail - i - 1) /
                                static_cast<int64_t>(tail));
                        }
                }
                ++underruns_;
                request_from_callback();
            }
        }
        // Release only AFTER all finite-support reads for committed output;
        // retain past FIR support separately from the next source centre.
        if (rd > wr) rd = wr;
        render_cursor_ = rd;
        const uint64_t release = recovery_owned || !options.linear ? rd :
            (rd - history_anchor_ > StreamingSinc::past ? rd - StreamingSinc::past : history_anchor_);
        released_ += release - start;
#ifdef DESKFUSE_TEST_PROBE
        if (recovery_owned) probe(RenderProbe::BeforeRecoveryTailRelease);
#endif
        read_.store(release, std::memory_order_release);
        if (recovery_owned) {
            // Acknowledgement promises that discarded storage is reusable.
            // Keep producers excluded until BOTH tail and ticket are visible.
            recovery_applied_.store(target_ticket, std::memory_order_release);
            publication_state_.store(kIdle, std::memory_order_release);
        }
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
        record.earliest_needed = release;
        record.published_tail = wr;
        record.fraction_q32 = last_fraction_;
        record.epoch = last_commit_.epoch;
        record.stream = last_commit_.stream;
        record.format = last_commit_.format;
        record.connection = last_commit_.connection;
        record.playout = last_commit_.playout;
        record.drift_ppm = drift_ppm_;
        // Legacy fields are published atomically for ABI v1/v2/v3
        // compatibility. They are NOT the coherent v4 callback record.
        legacy_rendered_.store(rendered_, std::memory_order_release);
        legacy_silence_.store(silence_, std::memory_order_release);
        legacy_underruns_.store(underruns_, std::memory_order_release);
        legacy_reprimes_.store(reprimes_, std::memory_order_release);
        legacy_released_.store(released_, std::memory_order_release);
        legacy_discarded_.store(discarded_, std::memory_order_release);
        legacy_concealed_.store(concealed_outputs_, std::memory_order_release);
        legacy_source_.store(last_commit_.source_frame, std::memory_order_release);
        legacy_phase_.store(last_fraction_, std::memory_order_release);
        legacy_epoch_.store(last_commit_.epoch, std::memory_order_release);
        legacy_playout_.store(last_commit_.playout, std::memory_order_release);
        legacy_ppm_.store(drift_ppm_, std::memory_order_release);
        snapshots_.publish(record);
        // Atomic payload: bounded readers never race with plain callback data.
        callback_cursor_.store(rd, std::memory_order_relaxed);
        callback_primed_.store(primed_ ? 1u : 0u, std::memory_order_relaxed);
        callback_frames_.store(count, std::memory_order_relaxed);
        max_callback_frames_ = std::max(max_callback_frames_, count);
        callback_max_frames_.store(max_callback_frames_, std::memory_order_relaxed);
        callback_future_.store(required_future, std::memory_order_relaxed);
        callback_source_.store(primed_ && rd < wr ? tags_[rd % capacity].source_frame : 0u,
            std::memory_order_relaxed);
        callback_source_valid_.store(primed_ && rd < wr ? 1u : 0u, std::memory_order_relaxed);
        callback_fraction_.store(fraction_q32_, std::memory_order_relaxed);
        callback_generation_.store(playout_generation_.load(std::memory_order_relaxed), std::memory_order_relaxed);
        callback_clipped_.store(clipped_samples_, std::memory_order_relaxed);
        playout_sequence_.fetch_add(1, std::memory_order_release);
    }
    bool playout_state(PlayoutState& out) const noexcept {
        const uint64_t before = playout_sequence_.load(std::memory_order_acquire);
        if (before & 1u) return false;
        PlayoutState value{};
        const uint64_t cursor = callback_cursor_.load(std::memory_order_relaxed);
        const uint64_t wr = write_.load(std::memory_order_acquire);
        const uint32_t future = callback_future_.load(std::memory_order_relaxed);
        value.queued_playable_frames = wr >= cursor && wr - cursor <= capacity ?
            (wr - cursor > future ? wr - cursor - future : 0) : 0;
        value.current_source_frame = callback_source_.load(std::memory_order_relaxed);
        value.primed = callback_primed_.load(std::memory_order_relaxed);
        value.last_callback_frames = callback_frames_.load(std::memory_order_relaxed);
        value.max_callback_frames = callback_max_frames_.load(std::memory_order_relaxed);
        value.playout_generation = callback_generation_.load(std::memory_order_relaxed);
        value.fraction_q32 = callback_fraction_.load(std::memory_order_relaxed);
        value.source_valid = callback_source_valid_.load(std::memory_order_relaxed);
        value.clipped_samples = callback_clipped_.load(std::memory_order_relaxed);
        value.requested_recovery = requested_recoveries();
        value.applied_recovery = applied_recoveries();
        std::atomic_thread_fence(std::memory_order_acquire);
        if (before != playout_sequence_.load(std::memory_order_acquire)) return false;
        out = value;
        return true;
    }
    uint64_t legacy_rendered() const noexcept { return legacy_rendered_.load(std::memory_order_acquire); }
    uint64_t legacy_silence() const noexcept { return legacy_silence_.load(std::memory_order_acquire); }
    uint64_t legacy_underruns() const noexcept { return legacy_underruns_.load(std::memory_order_acquire); }
    uint64_t legacy_reprimes() const noexcept { return legacy_reprimes_.load(std::memory_order_acquire); }
    uint64_t legacy_released() const noexcept { return legacy_released_.load(std::memory_order_acquire); }
    uint64_t legacy_discarded() const noexcept { return legacy_discarded_.load(std::memory_order_acquire); }
    uint64_t legacy_concealed() const noexcept { return legacy_concealed_.load(std::memory_order_acquire); }
    uint64_t legacy_source() const noexcept { return legacy_source_.load(std::memory_order_acquire); }
    uint32_t legacy_phase() const noexcept { return legacy_phase_.load(std::memory_order_acquire); }
    uint32_t legacy_epoch() const noexcept { return legacy_epoch_.load(std::memory_order_acquire); }
    uint32_t legacy_playout() const noexcept { return legacy_playout_.load(std::memory_order_acquire); }
    int32_t legacy_ppm() const noexcept { return legacy_ppm_.load(std::memory_order_acquire); }
    bool pop_snapshot(CoreSnapshot& record) noexcept {
        return snapshots_.pop(record);
    }
    uint64_t dropped_snapshots() const noexcept { return snapshots_.dropped(); }
#ifdef DESKFUSE_TEST_PROBE
    // Deterministic corruption injection *only* in host regression builds.
    // Production publisher cannot write incompatible support tags.
    void test_set_tag(uint64_t slot, MediaIdentity value) noexcept {
        tags_[slot % capacity] = value;
    }
    using Probe = void (*)(void*, RenderProbe) noexcept;
    // Install only before either owner starts; absent from production builds.
    void test_set_probe(Probe hook, void* context) noexcept {
        probe_ = hook;
        probe_context_ = context;
    }
#endif
    uint64_t published() const noexcept { return published_.load(std::memory_order_relaxed); }
    uint64_t overflow() const noexcept { return overflow_.load(std::memory_order_relaxed); }
    uint64_t read() const noexcept { return read_.load(std::memory_order_acquire); }
    uint64_t write() const noexcept { return write_.load(std::memory_order_acquire); }

private:
#ifdef DESKFUSE_TEST_PROBE
    void probe(RenderProbe point) noexcept {
        if (probe_) probe_(probe_context_, point);
    }
    Probe probe_ = nullptr;
    void* probe_context_ = nullptr;
    // Keep the host-only seam on a complete cache line before aligned PCM.
    // This also avoids implicit alignment padding under MSVC /W4 /WX.
    std::array<uint8_t, 64 - sizeof(Probe) - sizeof(void*)> probe_padding_{};
#endif
    static bool same_source_timeline(const MediaIdentity& a,
                                     const MediaIdentity& b) noexcept {
        // Local playout reset must never make old wire source positions fresh.
        return a.epoch == b.epoch && a.stream == b.stream &&
            a.format == b.format && a.connection == b.connection;
    }
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
    void reset_output(uint64_t anchor) noexcept {
        primed_ = false;
        fade_remaining_ = fade_frames;
        follower_.reset();
        fraction_q32_ = 0;
        drift_ppm_ = 0;
        playout_generation_.fetch_add(1, std::memory_order_acq_rel);
        last_commit_valid_ = false;
        history_anchor_ = anchor;
        render_cursor_ = anchor;
        last_kind_valid_ = false;
    }
    alignas(64) std::array<int16_t, capacity * kChannels> pcm_{};
    std::array<MediaIdentity, capacity> tags_{};
    std::atomic<uint64_t> read_{0}, write_{0};
    std::atomic<uint64_t> recovery_ticket_{0}, recovery_applied_{0};
    // A single try-transition, never a spin/wait: producer reservation and
    // callback reset cannot pass one another. Normal rendering stays SPSC.
    std::atomic<uint32_t> publication_state_{kIdle};
    std::atomic<uint32_t> playout_generation_{1};
    std::atomic<uint64_t> published_{0}, overflow_{0};
    // Producer-only fields:
    bool has_expected_ = false, producer_anchor_needed_ = true;
    uint64_t expected_source_ = 0;
    uint64_t last_observed_recovery_ = 0;
    MediaIdentity last_published_{};
    // Callback-only:
    bool primed_ = false, ever_primed_ = false, last_commit_valid_ = false;
    uint32_t fade_remaining_ = fade_frames, fraction_q32_ = 0, last_fraction_ = 0;
    MediaIdentity last_commit_{}, last_support_{};
    TimeBasedFollower follower_{};
    StreamingSinc sinc_{};
    uint64_t render_cursor_ = 0, history_anchor_ = 0;
    uint64_t clipped_samples_ = 0;
    std::array<uint16_t, capacity+1> conceal_prefix_{};
    bool last_kind_valid_ = false, last_kind_concealed_ = false;
    uint32_t max_callback_frames_ = 0;
    uint64_t callbacks_ = 0, committed_output_ = 0, rendered_ = 0;
    uint64_t silence_ = 0, released_ = 0, discarded_ = 0;
    uint64_t concealed_outputs_ = 0, underruns_ = 0, reprimes_ = 0;
    uint64_t invalidations_ = 0;
    int32_t drift_ppm_ = 0;
    SnapshotMailbox snapshots_{};
    std::atomic<uint64_t> legacy_rendered_{0}, legacy_silence_{0};
    std::atomic<uint64_t> legacy_underruns_{0}, legacy_reprimes_{0};
    std::atomic<uint64_t> legacy_released_{0}, legacy_discarded_{0};
    std::atomic<uint64_t> legacy_concealed_{0}, legacy_source_{0};
    std::atomic<uint32_t> legacy_phase_{0}, legacy_epoch_{0}, legacy_playout_{0};
    std::atomic<int32_t> legacy_ppm_{0};
    std::atomic<uint64_t> playout_sequence_{0}, callback_cursor_{0}, callback_source_{0};
    std::atomic<uint64_t> callback_clipped_{0};
    std::atomic<uint32_t> callback_primed_{0}, callback_frames_{0}, callback_max_frames_{0};
    std::atomic<uint32_t> callback_future_{StreamingSinc::future}, callback_source_valid_{0};
    std::atomic<uint32_t> callback_fraction_{0}, callback_generation_{1};
};
} // namespace df_audio
