// DeskFuse M2b Android platform adapter. ALL PCM publication, interpolation,
// identity commitment, release and callback recovery live in render_core.h,
// which is also the exact implementation compiled by host qualification.
#include <aaudio/AAudio.h>
#include "render_core.h"
#include "output_config.h"
#include "timing_sample.h"
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <new>
#include <time.h>

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
struct PresentationTimeV5 {
    int64_t output_frame_position;
    int64_t monotonic_nanoseconds;
    int32_t aaudio_result;
    int32_t reserved;
};
static_assert(sizeof(PresentationTimeV5) == 24);

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
std::atomic<uint64_t> next_output_generation{1};
class Renderer {
public:
    explicit Renderer(uint32_t reserve) noexcept : configured_reserve(reserve), core(reserve) {}
    const uint32_t configured_reserve;
    AAudioStream* stream = nullptr;
    uint64_t output_generation = 0; // immutable for this stream lifetime
    df_audio::RenderCore core;
    std::atomic<uint32_t> modes{3};
    std::atomic<int32_t> stream_error{AAUDIO_OK};
    // v1/v2/v3 records remain ABI-stable; v4 is the coherent record.
    std::atomic<uint64_t> callbacks{0};
    bool measure_callbacks = false; // immutable after lifecycle setup
    std::array<std::atomic<uint64_t>,6> size_buckets{};
    std::atomic<uint64_t> measured_calls{0}, total_wall_ns{0}, maximum_wall_ns{0}, clock_failures{0};

    static aaudio_data_callback_result_t callback(
        AAudioStream*, void* arg, void* audio, int32_t frames) noexcept {
        auto* r = static_cast<Renderer*>(arg);
        if (frames <= 0) return AAUDIO_CALLBACK_RESULT_CONTINUE;
        timespec before{},after{};
        const bool before_ok = r->measure_callbacks && clock_gettime(CLOCK_MONOTONIC,&before)==0;
        const uint32_t options = r->modes.load(std::memory_order_acquire);
        r->core.render(static_cast<int16_t*>(audio),
            static_cast<uint32_t>(frames),
            {(options & 1u) != 0, (options & 2u) != 0});
        if (r->measure_callbacks) {
            const bool after_ok=clock_gettime(CLOCK_MONOTONIC,&after)==0;
            const int64_t elapsed=(int64_t(after.tv_sec)-before.tv_sec)*1000000000+after.tv_nsec-before.tv_nsec;
            if (before_ok && after_ok && elapsed>=0) {
                r->measured_calls.fetch_add(1,std::memory_order_relaxed);
                r->total_wall_ns.fetch_add(uint64_t(elapsed),std::memory_order_relaxed);
                const auto old=r->maximum_wall_ns.load(std::memory_order_relaxed);
                if(uint64_t(elapsed)>old)r->maximum_wall_ns.store(uint64_t(elapsed),std::memory_order_relaxed);
            } else r->clock_failures.fetch_add(1,std::memory_order_relaxed);
        }
        const size_t bucket=frames<=96?0:frames<=240?1:frames<=480?2:frames<=960?3:frames<=1920?4:5;
        r->size_buckets[bucket].fetch_add(1,std::memory_order_relaxed);
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
        // Legacy occupancy is an always-available conservative storage upper
        // bound, including retained FIR history. Read tail FIRST: callback
        // progress can only reduce this estimate while producer publication
        // can increase it. ABI6 separately reports future playable demand.
        const uint64_t rd = core.read(), wr = core.write();
        s->queued_frames = wr >= rd ? std::min(wr-rd,df_audio::kRingFrames) : df_audio::kRingFrames;
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
DF_EXPORT uint32_t df_audio_abi_version() noexcept { return 8; }

static int32_t open_audio(void** out, const df_audio::OutputConfigV8& config) noexcept {
    if (!out) return -1;
    *out = nullptr;
    std::unique_ptr<Renderer> r(new (std::nothrow) Renderer(config.reserve_frames));
    if (!r) return -2;
    const char* measure=std::getenv("DESKFUSE_AUDIO_MEASURE_CALLBACK");
    if(measure && std::strcmp(measure,"0") && std::strcmp(measure,"1"))return -6;
    r->measure_callbacks=measure && !std::strcmp(measure,"1");
    AAudioStreamBuilder* builder = nullptr;
    const auto created = AAudio_createStreamBuilder(&builder);
    if (created != AAUDIO_OK) return created;
    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setSampleRate(builder, 48000);
    AAudioStreamBuilder_setChannelCount(builder, 2);
    AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
    AAudioStreamBuilder_setSharingMode(builder, config.sharing_policy == 1 ?
        AAUDIO_SHARING_MODE_EXCLUSIVE : AAUDIO_SHARING_MODE_SHARED);
    if (config.usage_policy == 1) AAudioStreamBuilder_setUsage(builder, AAUDIO_USAGE_GAME);
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
    const int32_t requested = df_audio::requested_buffer_frames(
        config.buffer_bursts, AAudioStream_getFramesPerBurst(r->stream));
    if (requested < 0) { AAudioStream_close(r->stream); return -6; }
    const int32_t set_result = requested == 0 ?
        AAudioStream_getBufferSizeInFrames(r->stream) :
        AAudioStream_setBufferSizeInFrames(r->stream, requested);
    if (set_result < 0) {
        std::fprintf(stderr, "AUDIO_BUFFER request_bursts=%u requested_frames=%d setter_error=%d\n",
            config.buffer_bursts, requested, set_result);
        AAudioStream_close(r->stream);
        return set_result;
    }
    // Lifecycle thread only: requesting LOW_LATENCY does not prove the
    // negotiated backend/buffer. Preserve actual hardware-route evidence.
    std::fprintf(stderr,
        "AUDIO_BACKEND rate=%d channels=%d format=%d sharing=%d performance=%d frames_per_burst=%d buffer_frames=%d capacity_frames=%d xruns=%d requested_bursts=%u requested_frames=%d setter_result=%d\n",
        AAudioStream_getSampleRate(r->stream),AAudioStream_getChannelCount(r->stream),
        AAudioStream_getFormat(r->stream),AAudioStream_getSharingMode(r->stream),
        AAudioStream_getPerformanceMode(r->stream),AAudioStream_getFramesPerBurst(r->stream),
        AAudioStream_getBufferSizeInFrames(r->stream),AAudioStream_getBufferCapacityInFrames(r->stream),
        AAudioStream_getXRunCount(r->stream), config.buffer_bursts, requested, set_result);
    std::fprintf(stderr,
        "AUDIO_CONFIG sharing_request=%u usage_policy=%u actual_usage=%d reserve_frames=%u "
        "configured_callback_frames=%d device=%d\n", config.sharing_policy, config.usage_policy,
        AAudioStream_getUsage(r->stream), config.reserve_frames,
        AAudioStream_getFramesPerDataCallback(r->stream), AAudioStream_getDeviceId(r->stream));
    const int32_t known_demand = AAudioStream_getFramesPerDataCallback(r->stream);
    if (known_demand > 0 && uint64_t(known_demand) + config.reserve_frames +
        df_audio::StreamingSinc::past > df_audio::RenderCore::capacity) {
        AAudioStream_close(r->stream);
        return -7;
    }
    const auto started = AAudioStream_requestStart(r->stream);
    if (started != AAUDIO_OK) {
        AAudioStream_close(r->stream);
        return started;
    }
    r->output_generation = next_output_generation.fetch_add(1, std::memory_order_relaxed);
    *out = r.release();
    return 0;
}
DF_EXPORT int32_t df_audio_open(void** out) noexcept { return open_audio(out, {}); }
DF_EXPORT int32_t df_audio_open_config_v7(
    const df_audio::OutputConfigV7* config, void** out) noexcept {
    if (!out) return -1;
    *out = nullptr;
    if (!config || !df_audio::valid_output_config(*config)) return -6;
    df_audio::OutputConfigV8 current;
    current.buffer_bursts = config->buffer_bursts;
    return open_audio(out, current);
}
DF_EXPORT int32_t df_audio_open_config_v8(
    const df_audio::OutputConfigV8* config, void** out) noexcept {
    if (!out) return -1;
    *out = nullptr;
    if (!config || !df_audio::valid_output_config(*config)) return -6;
    return open_audio(out, *config);
}
DF_EXPORT uint32_t df_audio_unsupported_demand_v8(void* ctx) noexcept {
    return ctx ? static_cast<Renderer*>(ctx)->core.unsupported_demand() : 0;
}
DF_EXPORT int32_t df_audio_callback_metrics_v8(void* ctx,df_audio::CallbackMetricsV8* out) noexcept {
    if(!ctx||!out)return -1;
    auto* r=static_cast<Renderer*>(ctx);*out={};
    for(size_t i=0;i<6;++i)out->size_buckets[i]=r->size_buckets[i].load(std::memory_order_relaxed);
    out->measurement_enabled=r->measure_callbacks?1:0;
    out->measured_calls=r->measured_calls.load(std::memory_order_relaxed);
    out->total_wall_ns=r->total_wall_ns.load(std::memory_order_relaxed);
    out->maximum_wall_ns=r->maximum_wall_ns.load(std::memory_order_relaxed);
    out->clock_failures=r->clock_failures.load(std::memory_order_relaxed);
    return 0; // pre-close aggregate observation, not an atomic callback snapshot
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
// Off-callback demand snapshot; no telemetry FIFO dependency. A concurrent
// callback returns unavailable, so the owner must choose conservative timing.
DF_EXPORT int32_t df_audio_playout_state_v6(void* ctx, void* out) noexcept {
    if (!ctx || !out) return -1;
    auto* r = static_cast<Renderer*>(ctx);
    auto* state = static_cast<df_audio::PlayoutState*>(out);
    if (!r->core.playout_state(*state)) return 0;
    state->stream_error = r->stream_error.load(std::memory_order_acquire);
    state->xrun_count = r->stream ? AAudioStream_getXRunCount(r->stream) : -1;
    return 1;
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
// Off-callback only: Android HAL presentation timestamp, NOT a remote
// WASAPI QPC or externally measured acoustic arrival time. The two clocks
// cannot be subtracted without a calibrated cross-machine time relation.
DF_EXPORT int32_t df_audio_presentation_time_v5(void* ctx, void* out) noexcept {
    if (!ctx || !out) return -1;
    auto* renderer = static_cast<Renderer*>(ctx);
    auto* result = static_cast<PresentationTimeV5*>(out);
    *result = {};
    if (!renderer->stream) return -2;
    result->aaudio_result = AAudioStream_getTimestamp(
        renderer->stream, CLOCK_MONOTONIC,
        &result->output_frame_position, &result->monotonic_nanoseconds);
    return 0;
}
// Owner/lifecycle thread only. No explicit wait, allocation or callback changes.
DF_EXPORT int32_t df_audio_timing_sample_v7(
    void* ctx, df_audio::TimingSampleV7* out) noexcept {
    if (!ctx || !out || out->size != sizeof(*out) || out->version != 7 || out->reserved != 0)
        return -1;
    auto* r = static_cast<Renderer*>(ctx);
    if (!r->stream) return -2;
    *out = {};
    out->output_generation = r->output_generation;
    timespec before{}, after{};
    const int before_result = clock_gettime(CLOCK_MONOTONIC, &before);
    out->state_before = AAudioStream_getState(r->stream);
    out->written_before = AAudioStream_getFramesWritten(r->stream);
    out->timestamp_result = AAudioStream_getTimestamp(r->stream, CLOCK_MONOTONIC,
        &out->timestamp_frame, &out->timestamp_ns);
    out->written_after = AAudioStream_getFramesWritten(r->stream);
    out->state_after = AAudioStream_getState(r->stream);
    out->sample_rate = AAudioStream_getSampleRate(r->stream);
    out->xrun_count = AAudioStream_getXRunCount(r->stream);
    out->buffer_frames = AAudioStream_getBufferSizeInFrames(r->stream);
    out->capacity_frames = AAudioStream_getBufferCapacityInFrames(r->stream);
    out->device_id = AAudioStream_getDeviceId(r->stream);
    const int after_result = clock_gettime(CLOCK_MONOTONIC, &after);
    out->clock_result = before_result != 0 ? -1 : (after_result != 0 ? -2 : 0);
    if (before_result == 0) out->before_ns = int64_t(before.tv_sec)*1000000000 + before.tv_nsec;
    if (after_result == 0) out->after_ns = int64_t(after.tv_sec)*1000000000 + after.tv_nsec;
    return 0; // Timestamp/clock failures stay explicit data, not fatal audio errors.
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

// Additive observation API; historical renderer ABI layouts remain unchanged.
struct OutputStateV1 { uint32_t size,version; int32_t sharing,performance,frames_per_burst,usage,format,reserve_frames; };
static_assert(sizeof(OutputStateV1)==32);
DF_EXPORT int32_t df_audio_output_state_v1(void* ctx,OutputStateV1* out) noexcept {
    if (!ctx || !out || out->size!=sizeof(OutputStateV1) || out->version!=1) return -1;
    auto* r=static_cast<Renderer*>(ctx);
    out->sharing=AAudioStream_getSharingMode(r->stream);
    out->performance=AAudioStream_getPerformanceMode(r->stream);
    out->frames_per_burst=AAudioStream_getFramesPerBurst(r->stream);
    out->usage=AAudioStream_getUsage(r->stream);
    out->format=AAudioStream_getFormat(r->stream);
    out->reserve_frames=static_cast<int32_t>(r->configured_reserve);
    return 0;
}
