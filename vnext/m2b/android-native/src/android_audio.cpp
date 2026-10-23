// DeskFuse M2b Android platform adapter. ALL PCM publication, interpolation,
// identity commitment, release and callback recovery live in render_core.h,
// which is also the exact implementation compiled by host qualification.
#include <aaudio/AAudio.h>
#include "render_core.h"
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <new>

namespace {
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
class Renderer {
public:
    AAudioStream* stream = nullptr;
    df_audio::RenderCore core;
    std::atomic<uint32_t> modes{3};
    std::atomic<int32_t> stream_error{AAUDIO_OK};
    // v1/v2/v3 records remain ABI-stable; v4 is the coherent record.
    std::atomic<uint64_t> callbacks{0};

    static aaudio_data_callback_result_t callback(
        AAudioStream*, void* arg, void* audio, int32_t frames) noexcept {
        auto* r = static_cast<Renderer*>(arg);
        if (frames <= 0) return AAUDIO_CALLBACK_RESULT_CONTINUE;
        const uint32_t options = r->modes.load(std::memory_order_acquire);
        r->core.render(static_cast<int16_t*>(audio),
            static_cast<uint32_t>(frames),
            {(options & 1u) != 0, (options & 2u) != 0});
        // These mirrors serve old ABI callers only; coherent consumers use
        // df_audio_snapshot_v4() and the bounded off-callback queue.
        // Gather the callback's exact counters by its own nonblocking mailbox
        // would steal snapshots from the diagnostic consumer, so the mirror
        // instead tracks counts directly from published diagnostic copies.
        r->callbacks.fetch_add(1, std::memory_order_relaxed);
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }
    static void error(AAudioStream*, void* arg, aaudio_result_t code) noexcept {
        static_cast<Renderer*>(arg)->stream_error.store(code, std::memory_order_release);
    }
    void stats(Stats* s) noexcept {
        s->accepted_frames = core.published();
        s->rendered_frames = core.legacy_rendered();
        s->silence_frames = core.legacy_silence();
        s->overflow_packets = core.overflow();
        s->callback_count = callbacks.load(std::memory_order_relaxed);
        const uint64_t wr = core.write(), rd = core.read();
        s->queued_frames = wr >= rd && wr - rd <= df_audio::kRingFrames ? wr - rd : 0;
        s->stream_error = stream_error.load(std::memory_order_acquire);
        s->actual_sample_rate = stream ? AAudioStream_getSampleRate(stream) : 0;
        s->actual_channels = stream ? AAudioStream_getChannelCount(stream) : 0;
        s->drift_correction_ppm = core.legacy_ppm();
        s->underrun_events = core.legacy_underruns();
        s->reprime_events = core.legacy_reprimes();
    }
};
} // namespace

#if defined(__GNUC__)
#define DF_EXPORT extern "C" __attribute__((visibility("default")))
#else
#define DF_EXPORT extern "C"
#endif
DF_EXPORT uint32_t df_audio_abi_version() noexcept { return 5; }

DF_EXPORT int32_t df_audio_open(void** out) noexcept {
    if (!out) return -1;
    *out = nullptr;
    std::unique_ptr<Renderer> r(new (std::nothrow) Renderer{});
    if (!r) return -2;
    AAudioStreamBuilder* builder = nullptr;
    const auto created = AAudio_createStreamBuilder(&builder);
    if (created != AAUDIO_OK) return created;
    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setSampleRate(builder, 48000);
    AAudioStreamBuilder_setChannelCount(builder, 2);
    AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
    AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
    AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
    AAudioStreamBuilder_setDataCallback(builder, &Renderer::callback, r.get());
    AAudioStreamBuilder_setErrorCallback(builder, &Renderer::error, r.get());
    const auto result = AAudioStreamBuilder_openStream(builder, &r->stream);
    AAudioStreamBuilder_delete(builder);
    if (result != AAUDIO_OK) return result;
    if (AAudioStream_getSampleRate(r->stream) != 48000 ||
        AAudioStream_getChannelCount(r->stream) != 2 ||
        AAudioStream_getFormat(r->stream) != AAUDIO_FORMAT_PCM_I16) {
        AAudioStream_close(r->stream);
        return -5;
    }
    const auto started = AAudioStream_requestStart(r->stream);
    if (started != AAUDIO_OK) {
        AAudioStream_close(r->stream);
        return started;
    }
    *out = r.release();
    return 0;
}
DF_EXPORT int32_t df_audio_set_diagnostic_modes(void* ctx, uint32_t flags) noexcept {
    if (!ctx || (flags & ~3u)) return -1;
    auto* r = static_cast<Renderer*>(ctx);
    if (r->core.published()) return -2;
    r->modes.store(flags, std::memory_order_release);
    return 0;
}
DF_EXPORT int32_t df_audio_push_v4(
    void* ctx, const int16_t* pcm, uint32_t frames, uint64_t source,
    uint32_t epoch, uint32_t stream, uint32_t format,
    uint32_t connection, uint32_t kind) noexcept {
    if (!ctx) return -1;
    return static_cast<int32_t>(static_cast<Renderer*>(ctx)->core.publish(
        pcm, frames, {source, epoch, stream, format, connection, 0,
                       static_cast<uint8_t>(kind)}));
}
// ABI compatibility: old publication methods are retained; the new receiver
// authorizes each block against a specific completed recovery. Record layouts
// and historical entry points remain unchanged.
DF_EXPORT int32_t df_audio_push_guarded_v5(
    void* ctx, const int16_t* pcm, uint32_t frames, uint64_t source,
    uint32_t epoch, uint32_t stream, uint32_t format,
    uint32_t connection, uint32_t kind, uint64_t authorized_recovery) noexcept {
    if (!ctx || kind > 1) return -2;
    return static_cast<int32_t>(static_cast<Renderer*>(ctx)->core.publish_guarded(
        pcm, frames, {source, epoch, stream, format, connection, 0,
                      static_cast<uint8_t>(kind)}, authorized_recovery));
}
// ABI compatibility: old publication methods are retained; the new receiver
// uses only push_v4. Both old forms refer to one isolated stream/context.
DF_EXPORT int32_t df_audio_push_identified(
    void* ctx, const int16_t* pcm, uint32_t frames, uint64_t source,
    uint32_t epoch, uint32_t generation, uint32_t kind) noexcept {
    return df_audio_push_v4(ctx, pcm, frames, source, epoch, generation, 1, 1, kind);
}
DF_EXPORT int32_t df_audio_push(void* ctx, const int16_t* pcm, uint32_t frames) noexcept {
    if (!ctx) return -1;
    const auto* r = static_cast<Renderer*>(ctx);
    return df_audio_push_v4(ctx, pcm, frames, r->core.write(), 1, 1, 1, 1, 0);
}
DF_EXPORT uint64_t df_audio_request_recovery_v4(void* ctx) noexcept {
    if (!ctx) return 0;
    auto& core = static_cast<Renderer*>(ctx)->core;
    core.invalidate();
    return core.requested_recoveries();
}
DF_EXPORT uint64_t df_audio_recovery_applied_v4(void* ctx) noexcept {
    if (!ctx) return 0;
    return static_cast<Renderer*>(ctx)->core.applied_recoveries();
}
DF_EXPORT int32_t df_audio_snapshot_v4(
    void* ctx, df_audio::CoreSnapshot* snapshot, uint64_t* dropped) noexcept {
    if (!ctx || !snapshot || !dropped) return -1;
    auto& core = static_cast<Renderer*>(ctx)->core;
    *dropped = core.dropped_snapshots();
    return core.pop_snapshot(*snapshot) ? 1 : 0;
}
DF_EXPORT int32_t df_audio_stats(void* ctx, void* out) noexcept {
    if (!ctx || !out) return -1;
    Stats s{};
    static_cast<Renderer*>(ctx)->stats(&s);
    static_assert(offsetof(Stats, drift_correction_ppm) == 60);
    std::memcpy(out, &s, offsetof(Stats, drift_correction_ppm));
    return 0;
}
DF_EXPORT int32_t df_audio_stats_v2(void* ctx, void* out) noexcept {
    if (!ctx || !out) return -1;
    static_cast<Renderer*>(ctx)->stats(static_cast<Stats*>(out));
    return 0;
}
DF_EXPORT int32_t df_audio_identity_stats_v3(void* ctx, void* out) noexcept {
    if (!ctx || !out) return -1;
    auto* r = static_cast<Renderer*>(ctx);
    auto* s = static_cast<IdentityStats*>(out);
    s->last_committed_source_frame = r->core.legacy_source();
    s->last_fraction_q32 = r->core.legacy_phase();
    s->last_epoch = r->core.legacy_epoch();
    s->last_generation = r->core.legacy_playout();
    s->reserved = 0;
    s->released_source_frames = r->core.legacy_released();
    s->discarded_on_rebuffer_frames = r->core.legacy_discarded();
    s->concealed_output_frames = r->core.legacy_concealed();
    return 0;
}
DF_EXPORT void df_audio_close(void* ctx) noexcept {
    auto* r = static_cast<Renderer*>(ctx);
    if (!r) return;
    if (r->stream) {
        AAudioStream_requestStop(r->stream);
        AAudioStream_close(r->stream);
    }
    delete r;
}
