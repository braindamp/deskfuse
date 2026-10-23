// DeskFuse M2b — isolated Android AAudio speaker renderer.
// Native callback is the SINGLE consumer/output-clock authority.
// Rust QUIC network thread is the SINGLE producer. No allocations, blocking
// calls, locks, syscalls, or network operations in the audio callback.
// This is the initial audible integration, not yet a drift-qualified renderer.
#include <aaudio/AAudio.h>
#include "adaptive_clock.h"
#include "clock_qualification.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <memory>
#include <new>

namespace {

constexpr int32_t kSampleRate = 48000;
constexpr int32_t kChannels = 2;
constexpr uint32_t kBlockFrames = 240;
constexpr uint64_t kCapacityFrames = df_audio::kRingFrames; // hard cap: 80 ms
constexpr uint64_t kPrimeFrames = df_audio::kPrimeFrames; // 30 ms
constexpr uint32_t kFadeFrames = 96;                  // 2 ms click-reduction edge

// Tags occupy the same SPSC lifetime as their corresponding PCM samples.
struct SourceTag {
    uint64_t source_frame;
    uint32_t epoch;
    uint32_t generation;
    uint32_t kind; // 0=captured, 1=concealed
};
struct IdentityStats {
    uint64_t last_committed_source_frame;
    uint32_t last_fraction_q32;
    uint32_t last_epoch;
    uint32_t last_generation;
    uint32_t reserved;
    uint64_t released_source_frames;
    uint64_t discarded_on_rebuffer_frames;
    uint64_t concealed_output_frames;
};

struct Stats {
    uint64_t accepted_frames;
    uint64_t rendered_frames;
    uint64_t silence_frames;
    uint64_t overflow_packets;
    uint64_t callback_count;
    uint64_t queued_frames;
    int32_t stream_error;
    int32_t actual_sample_rate;
    int32_t actual_channels;
    int32_t drift_correction_ppm;
    uint64_t underrun_events;
    uint64_t reprime_events;
};

class Renderer {
public:
    AAudioStream* stream = nullptr;
    // Unwrapped monotonic SPSC positions. Producer publishes after writing.
    std::atomic<uint64_t> read_pos{0};
    std::atomic<uint64_t> write_pos{0};
    std::array<int16_t, kCapacityFrames * kChannels> ring{};
    std::array<SourceTag, kCapacityFrames> tags{};

    // Callback-private state: only the renderer's AAudio callback touches this.
    bool primed = false;
    bool has_primed = false;
    uint32_t fade_in_remaining = kFadeFrames; // callback thread only
    df_audio::DriftFollower follower; // Q32 phase and linear-interpolation comparator
    df_audio::TimeBasedFollower output_clock; // elapsed-time controller, callback-owned
    // Bits: 0=fade, 1=linear resampling. Bit 2 concealment belongs
    // exclusively to the Rust receiver, which owns source-frame gaps.
    std::atomic<uint32_t> diagnostic_modes{3};

    std::atomic<uint64_t> accepted_frames{0};
    std::atomic<uint64_t> rendered_frames{0};
    std::atomic<uint64_t> silence_frames{0};
    std::atomic<uint64_t> overflow_packets{0};
    std::atomic<uint64_t> callback_count{0};
    std::atomic<uint64_t> underrun_events{0};
    std::atomic<uint64_t> committed_source_frame{0};
    std::atomic<uint32_t> committed_fraction_q32{0};
    std::atomic<uint32_t> committed_epoch{0};
    std::atomic<uint32_t> committed_generation{0};
    std::atomic<uint64_t> released_source_frames{0};
    std::atomic<uint64_t> discarded_source_frames{0};
    std::atomic<uint64_t> concealed_output_frames{0};
    bool has_committed_identity = false; // callback only
    uint64_t callback_last_source_frame = 0;
    uint32_t callback_last_epoch = 0, callback_last_generation = 0;
    std::atomic<uint64_t> reprime_events{0};
    std::atomic<int32_t> current_correction_ppm{0};
    std::atomic<int32_t> stream_error{AAUDIO_OK};

    static aaudio_data_callback_result_t callback(
        AAudioStream*, void* user_data, void* audio_data, int32_t nframes) noexcept {
        auto* self = static_cast<Renderer*>(user_data);
        auto* output = static_cast<int16_t*>(audio_data);
        if (nframes <= 0) return AAUDIO_CALLBACK_RESULT_CONTINUE;
        const uint64_t requested = static_cast<uint64_t>(nframes);
        // Output thread is the sole media clock. Never wait for the network;
        // every frame outside the authenticated PCM range is zero-valued.
        std::memset(output, 0, static_cast<size_t>(requested) * kChannels * sizeof(int16_t));

        uint64_t rd = self->read_pos.load(std::memory_order_relaxed);
        const uint64_t wr = self->write_pos.load(std::memory_order_acquire);
        if (wr < rd || wr - rd > kCapacityFrames) {
            self->stream_error.store(-10001, std::memory_order_release);
            return AAUDIO_CALLBACK_RESULT_STOP;
        }
        const uint64_t ready = wr - rd;
        if (!self->primed) {
            if (ready >= kPrimeFrames) {
                self->primed = true;
                self->fade_in_remaining = kFadeFrames;
                self->follower.reset();
                self->output_clock.reset();
                if (self->has_primed) {
                    self->reprime_events.fetch_add(1, std::memory_order_relaxed);
                }
                self->has_primed = true;
            } else {
                self->silence_frames.fetch_add(requested, std::memory_order_relaxed);
                self->callback_count.fetch_add(1, std::memory_order_relaxed);
                return AAUDIO_CALLBACK_RESULT_CONTINUE;
            }
        }

        const uint32_t modes = self->diagnostic_modes.load(std::memory_order_acquire);
        uint64_t produced = 0;
        if ((modes & 2u) != 0) {
            // The controller integrates by OUTPUT FRAMES (elapsed sample time),
            // uses projected post-callback occupancy, clips disturbances,
            // limits correction slew and applies conditional anti-windup.
            // The linear interpolation below remains only an experimental
            // spectral comparator; it is NOT the production streaming ASRC.
            const double commanded_ppm = self->output_clock.update(
                static_cast<uint32_t>(requested),
                static_cast<int64_t>(ready),
                static_cast<int64_t>(df_audio::kTargetFrames));
            const auto delta_q32 = static_cast<int64_t>(
                static_cast<double>(df_audio::kOneQ32) *
                commanded_ppm / 1'000'000.0);
            const uint64_t step_q32 = static_cast<uint64_t>(
                static_cast<int64_t>(df_audio::kOneQ32) + delta_q32);
            self->current_correction_ppm.store(
                static_cast<int32_t>(commanded_ppm), std::memory_order_relaxed);
            while (produced < requested && wr - rd >= 2) {
                self->follower.consume_one(
                    self->ring.data(), rd,
                    output + static_cast<size_t>(produced) * kChannels,
                    step_q32);
                ++produced;
            }
        } else {
            // 1:1 no-resampling diagnostic, preserving source frame spacing.
            // The Q32 phase is intentionally reset by configure-before-media.
            produced = std::min(requested, ready);
            const uint64_t pos = rd % kCapacityFrames;
            const uint64_t first = std::min(produced, kCapacityFrames - pos);
            if (first) std::memcpy(output, self->ring.data() + pos * kChannels,
                                   static_cast<size_t>(first) * kChannels * sizeof(int16_t));
            if (produced > first)
                std::memcpy(output + first * kChannels, self->ring.data(),
                            static_cast<size_t>(produced - first) * kChannels * sizeof(int16_t));
            rd += produced;
            self->current_correction_ppm.store(0, std::memory_order_relaxed);
        }

        // The first and final few output frames are multiplied by a bounded
        // gain ramp to reduce abrupt edges. Do not change already committed
        // audio, real WASAPI source identity, or any QUIC send deadline.
        if ((modes & 1u) != 0 && self->fade_in_remaining && produced) {
            const auto ramp = std::min<uint64_t>(produced, self->fade_in_remaining);
            const uint32_t begun = kFadeFrames - self->fade_in_remaining;
            for (uint64_t frame = 0; frame < ramp; ++frame) {
                const int32_t gain = static_cast<int32_t>(begun + frame + 1);
                for (int ch = 0; ch < kChannels; ++ch) {
                    const auto i = static_cast<size_t>(frame) * kChannels + ch;
                    output[i] = static_cast<int16_t>(
                        static_cast<int32_t>(output[i]) * gain / kFadeFrames);
                }
            }
            self->fade_in_remaining -= static_cast<uint32_t>(ramp);
        }
        if ((modes & 1u) != 0 && produced < requested && produced) {
            const auto tail = std::min<uint64_t>(produced, kFadeFrames);
            const auto begin = produced - tail;
            for (uint64_t frame = 0; frame < tail; ++frame) {
                const int32_t gain = static_cast<int32_t>(tail - frame - 1);
                for (int ch = 0; ch < kChannels; ++ch) {
                    const auto i = static_cast<size_t>(begin + frame) * kChannels + ch;
                    output[i] = static_cast<int16_t>(
                        static_cast<int32_t>(output[i]) * gain /
                        static_cast<int32_t>(tail));
                }
            }
        }

        // Sole reader releases consumed slots once all interpolation reads
        // finish; producer sees the release before reusing that region.
        if (produced < requested) {
            // A partial callback has already played the newest contiguous
            // interval. Drop any isolated final lookahead frame (at most one),
            // reset the rate follower and prime fresh data before resuming.
            rd = wr;
            self->primed = false;
            self->follower.reset();
            self->output_clock.reset();
            self->current_correction_ppm.store(0, std::memory_order_relaxed);
            self->underrun_events.fetch_add(1, std::memory_order_relaxed);
        }
        self->read_pos.store(rd, std::memory_order_release);
        self->rendered_frames.fetch_add(produced, std::memory_order_relaxed);
        self->silence_frames.fetch_add(requested - produced, std::memory_order_relaxed);
        self->callback_count.fetch_add(1, std::memory_order_relaxed);
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }

    static void error_callback(AAudioStream*, void* user_data, aaudio_result_t error) noexcept {
        static_cast<Renderer*>(user_data)->stream_error.store(error, std::memory_order_release);
    }

    int32_t push(const int16_t* samples, uint32_t frames,
                 uint64_t first_source_frame, uint32_t epoch,
                 uint32_t generation, uint32_t kind) noexcept {
        if (!samples || frames != kBlockFrames || kind > 1 ||
            first_source_frame > UINT64_MAX - frames) return -2;
        if (stream_error.load(std::memory_order_acquire) != AAUDIO_OK) return -3;

        const uint64_t wr = write_pos.load(std::memory_order_relaxed);
        const uint64_t rd = read_pos.load(std::memory_order_acquire);
        if (wr < rd || wr - rd > kCapacityFrames) return -4;
        if (kCapacityFrames - (wr - rd) < frames) {
            overflow_packets.fetch_add(1, std::memory_order_relaxed);
            return 1; // dropped newest frame, never overwrite committed playback
        }
        const uint64_t pos = wr % kCapacityFrames;
        const uint64_t first = std::min<uint64_t>(frames, kCapacityFrames - pos);
        std::memcpy(ring.data() + pos * kChannels, samples,
                    static_cast<size_t>(first) * kChannels * sizeof(int16_t));
        if (frames > first) {
            std::memcpy(ring.data(), samples + first * kChannels,
                        static_cast<size_t>(frames - first) * kChannels * sizeof(int16_t));
        }
        // Tags are completed before the PCM publication release. The
        // callback reads them only after acquiring write_pos.
        for (uint32_t n = 0; n < frames; ++n) {
            tags[(wr + n) % kCapacityFrames] =
                SourceTag{first_source_frame + n, epoch, generation, kind};
        }
        write_pos.store(wr + frames, std::memory_order_release);
        accepted_frames.fetch_add(frames, std::memory_order_relaxed);
        return 0;
    }

    void identity_stats(IdentityStats* s) const noexcept {
        s->last_committed_source_frame =
            committed_source_frame.load(std::memory_order_acquire);
        s->last_fraction_q32 =
            committed_fraction_q32.load(std::memory_order_acquire);
        s->last_epoch = committed_epoch.load(std::memory_order_acquire);
        s->last_generation = committed_generation.load(std::memory_order_acquire);
        s->reserved = 0;
        s->released_source_frames =
            released_source_frames.load(std::memory_order_acquire);
        s->discarded_on_rebuffer_frames =
            discarded_source_frames.load(std::memory_order_acquire);
        s->concealed_output_frames =
            concealed_output_frames.load(std::memory_order_acquire);
    }
    void get_stats(Stats* s) const noexcept {
        s->accepted_frames = accepted_frames.load(std::memory_order_relaxed);
        s->rendered_frames = rendered_frames.load(std::memory_order_relaxed);
        s->silence_frames = silence_frames.load(std::memory_order_relaxed);
        s->overflow_packets = overflow_packets.load(std::memory_order_relaxed);
        s->callback_count = callback_count.load(std::memory_order_relaxed);
        const uint64_t wr = write_pos.load(std::memory_order_acquire);
        const uint64_t rd = read_pos.load(std::memory_order_acquire);
        s->queued_frames = wr >= rd && wr - rd <= kCapacityFrames ? wr - rd : 0;
        s->stream_error = stream_error.load(std::memory_order_acquire);
        s->actual_sample_rate = stream ? AAudioStream_getSampleRate(stream) : 0;
        s->actual_channels = stream ? AAudioStream_getChannelCount(stream) : 0;
        s->drift_correction_ppm = current_correction_ppm.load(std::memory_order_relaxed);
        s->underrun_events = underrun_events.load(std::memory_order_relaxed);
        s->reprime_events = reprime_events.load(std::memory_order_relaxed);
    }
};

} // namespace

#if defined(__GNUC__)
#define DF_EXPORT extern "C" __attribute__((visibility("default")))
#else
#define DF_EXPORT extern "C"
#endif

// Explicit C-ABI guard: an older .so must not write past a newer Rust Stats.
DF_EXPORT uint32_t df_audio_abi_version() noexcept { return 3; }

DF_EXPORT int32_t df_audio_open(void** output) noexcept {
    if (!output) return -1;
    *output = nullptr;
    std::unique_ptr<Renderer> r{new (std::nothrow) Renderer()};
    if (!r) return -2;

    AAudioStreamBuilder* builder = nullptr;
    aaudio_result_t result = AAudio_createStreamBuilder(&builder);
    if (result != AAUDIO_OK) return result;

    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setSampleRate(builder, kSampleRate);
    AAudioStreamBuilder_setChannelCount(builder, kChannels);
    AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
    AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
    AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
    AAudioStreamBuilder_setDataCallback(builder, &Renderer::callback, r.get());
    AAudioStreamBuilder_setErrorCallback(builder, &Renderer::error_callback, r.get());

    result = AAudioStreamBuilder_openStream(builder, &r->stream);
    AAudioStreamBuilder_delete(builder);
    if (result != AAUDIO_OK) return result;

    // Never silently play captured 48-kHz stereo PCM at another format.
    if (AAudioStream_getSampleRate(r->stream) != kSampleRate ||
        AAudioStream_getChannelCount(r->stream) != kChannels ||
        AAudioStream_getFormat(r->stream) != AAUDIO_FORMAT_PCM_I16) {
        AAudioStream_close(r->stream);
        r->stream = nullptr;
        return -5;
    }

    result = AAudioStream_requestStart(r->stream);
    if (result != AAUDIO_OK) {
        AAudioStream_close(r->stream);
        r->stream = nullptr;
        return result;
    }
    *output = r.release();
    return AAUDIO_OK;
}

// Call once after opening native audio but BEFORE publishing the first
// PCM block. No runtime playback mode switch or callback lock is permitted.
DF_EXPORT int32_t df_audio_set_diagnostic_modes(void* ctx, uint32_t flags) noexcept {
    if (!ctx || (flags & ~uint32_t{3}) != 0) return -1;
    auto* r = static_cast<Renderer*>(ctx);
    if (r->accepted_frames.load(std::memory_order_acquire) != 0) return -2;
    r->diagnostic_modes.store(flags, std::memory_order_release);
    return 0;
}

DF_EXPORT int32_t df_audio_push(void* ctx, const int16_t* pcm, uint32_t frames) noexcept {
    if (!ctx) return -1;
    const auto* r = static_cast<Renderer*>(ctx);
    const auto next = r->write_pos.load(std::memory_order_relaxed);
    return static_cast<Renderer*>(ctx)->push(
        pcm, frames, next, 0, 0, 0);
}

// ABI v1 statistic consumers allocate only the original prefix.
// Never write the v2 extension into an old caller's smaller buffer.
// First 9 fields are unchanged and are copied byte-for-byte.
DF_EXPORT int32_t df_audio_push_identified(
    void* ctx, const int16_t* pcm, uint32_t frames,
    uint64_t first_source_frame, uint32_t epoch, uint32_t generation,
    uint32_t kind) noexcept {
    if (!ctx) return -1;
    return static_cast<Renderer*>(ctx)->push(
        pcm, frames, first_source_frame, epoch, generation, kind);
}

// A new versioned entry point; the v1 and v2 stats records stay byte stable.
DF_EXPORT int32_t df_audio_identity_stats_v3(void* ctx, void* output) noexcept {
    if (!ctx || !output) return -1;
    static_cast<Renderer*>(ctx)->identity_stats(static_cast<IdentityStats*>(output));
    return 0;
}

DF_EXPORT int32_t df_audio_stats(void* ctx, void* output) noexcept {
    if (!ctx || !output) return -1;
    Stats snapshot{};
    static_cast<Renderer*>(ctx)->get_stats(&snapshot);
    static_assert(offsetof(Stats, drift_correction_ppm) == 60,
                  "ABI v1 statistic prefix layout changed");
    std::memcpy(output, &snapshot, offsetof(Stats, drift_correction_ppm));
    return 0;
}

// ABI v2 callers check df_audio_abi_version() before using this function.
// The full struct is written only when the caller allocated its new layout.
DF_EXPORT int32_t df_audio_stats_v2(void* ctx, void* output) noexcept {
    if (!ctx || !output) return -1;
    static_cast<Renderer*>(ctx)->get_stats(static_cast<Stats*>(output));
    return 0;
}

DF_EXPORT void df_audio_close(void* ctx) noexcept {
    auto* r = static_cast<Renderer*>(ctx);
    if (!r) return;
    if (r->stream) {
        AAudioStream_requestStop(r->stream);
        AAudioStream_close(r->stream); // callback no longer accesses r
        r->stream = nullptr;
    }
    delete r;
}
