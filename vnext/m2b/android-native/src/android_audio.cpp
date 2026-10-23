// DeskFuse M2b — isolated Android AAudio speaker renderer.
// Native callback is the SINGLE consumer/output-clock authority.
// Rust QUIC network thread is the SINGLE producer. No allocations, blocking
// calls, locks, syscalls, or network operations in the audio callback.
// This is the initial audible integration, not yet a drift-qualified renderer.
#include <aaudio/AAudio.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <memory>
#include <new>

namespace {

constexpr int32_t kSampleRate = 48000;
constexpr int32_t kChannels = 2;
constexpr uint32_t kBlockFrames = 240;
constexpr uint64_t kCapacityFrames = kBlockFrames * 16; // hard cap: 80 ms
constexpr uint64_t kPrimeFrames = kBlockFrames * 4;     // initial 20 ms

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
};

class Renderer {
public:
    AAudioStream* stream = nullptr;
    // Unwrapped monotonic SPSC positions. Producer publishes after writing.
    std::atomic<uint64_t> read_pos{0};
    std::atomic<uint64_t> write_pos{0};
    std::array<int16_t, kCapacityFrames * kChannels> ring{};

    // Callback-private state: only the renderer's AAudio callback touches this.
    bool primed = false;

    std::atomic<uint64_t> accepted_frames{0};
    std::atomic<uint64_t> rendered_frames{0};
    std::atomic<uint64_t> silence_frames{0};
    std::atomic<uint64_t> overflow_packets{0};
    std::atomic<uint64_t> callback_count{0};
    std::atomic<int32_t> stream_error{AAUDIO_OK};

    static aaudio_data_callback_result_t callback(
        AAudioStream*, void* user_data, void* audio_data, int32_t nframes) noexcept {
        auto* self = static_cast<Renderer*>(user_data);
        auto* output = static_cast<int16_t*>(audio_data);
        if (nframes <= 0) return AAUDIO_CALLBACK_RESULT_CONTINUE;
        const auto requested = static_cast<uint64_t>(nframes);
        // Always define the entire output; silence is the underrun policy.
        std::memset(output, 0, static_cast<size_t>(requested) * kChannels * sizeof(int16_t));

        const uint64_t rd = self->read_pos.load(std::memory_order_relaxed);
        const uint64_t wr = self->write_pos.load(std::memory_order_acquire);
        const uint64_t ready = wr - rd;
        // Verify the producer's bounded ring invariant without blocking.
        if (ready > kCapacityFrames) {
            self->stream_error.store(-10001, std::memory_order_release);
            return AAUDIO_CALLBACK_RESULT_STOP;
        }
        if (!self->primed) {
            if (ready >= kPrimeFrames) self->primed = true;
            else {
                self->silence_frames.fetch_add(requested, std::memory_order_relaxed);
                self->callback_count.fetch_add(1, std::memory_order_relaxed);
                return AAUDIO_CALLBACK_RESULT_CONTINUE;
            }
        }

        const uint64_t take = std::min(ready, requested);
        const uint64_t pos = rd % kCapacityFrames;
        const uint64_t first = std::min(take, kCapacityFrames - pos);
        if (first) {
            std::memcpy(output, self->ring.data() + pos * kChannels,
                        static_cast<size_t>(first) * kChannels * sizeof(int16_t));
        }
        if (take > first) {
            std::memcpy(output + first * kChannels, self->ring.data(),
                        static_cast<size_t>(take - first) * kChannels * sizeof(int16_t));
        }

        // Sole reader publishes release BEFORE producer reuses the slots.
        self->read_pos.store(rd + take, std::memory_order_release);
        self->rendered_frames.fetch_add(take, std::memory_order_relaxed);
        self->silence_frames.fetch_add(requested - take, std::memory_order_relaxed);
        self->callback_count.fetch_add(1, std::memory_order_relaxed);
        if (take < requested) self->primed = false; // re-prime after underrun
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }

    static void error_callback(AAudioStream*, void* user_data, aaudio_result_t error) noexcept {
        static_cast<Renderer*>(user_data)->stream_error.store(error, std::memory_order_release);
    }

    int32_t push(const int16_t* samples, uint32_t frames) noexcept {
        if (!samples || frames != kBlockFrames) return -2;
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
        write_pos.store(wr + frames, std::memory_order_release);
        accepted_frames.fetch_add(frames, std::memory_order_relaxed);
        return 0;
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
    }
};

} // namespace

#if defined(__GNUC__)
#define DF_EXPORT extern "C" __attribute__((visibility("default")))
#else
#define DF_EXPORT extern "C"
#endif

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

DF_EXPORT int32_t df_audio_push(void* ctx, const int16_t* pcm, uint32_t frames) noexcept {
    if (!ctx) return -1;
    return static_cast<Renderer*>(ctx)->push(pcm, frames);
}

DF_EXPORT int32_t df_audio_stats(void* ctx, void* output) noexcept {
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
