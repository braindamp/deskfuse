// Included by source_control.cpp to reuse the qualified conversion/period core.
#include "owned_loopback_abi.h"
#include <limits>

namespace {
struct OwnedLoopback {
    df_wasapi::SpscCaptureQueue<Block, kQueueSlots> queue;
    Handle ready{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    Handle terminal{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    Handle stop{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<uint32_t> flags{0};
    std::atomic<int32_t> error{S_OK};
    DeskFuseTimingCapabilities identity{}; // immutable after release-Ready
    uint32_t period_frames=0, capacity_frames=0;
    std::thread worker;
    bool joined=false; // lifecycle-owner confined
    NativeDropLedger dropped_positions; // producer only; reported after stop

    OwnedLoopback() {
        if (!ready.get() || !terminal.get() || !stop.get())
            throw std::runtime_error("loopback event creation failed");
    }
    bool stopping() const noexcept {
        return (flags.load(std::memory_order_acquire) & DF_LOOPBACK_STOP_REQUESTED) != 0;
    }
    void require_setup(uint64_t cutoff) const {
        ULONGLONG now=0; QueryInterruptTimePrecise(&now);
        if (stopping() || now>=cutoff) throw std::runtime_error("loopback setup cancelled/expired");
    }
    void fail(HRESULT hr) noexcept {
        error.store(hr, std::memory_order_relaxed);
        flags.fetch_or(DF_LOOPBACK_FAILED, std::memory_order_release);
    }
    void complete() noexcept {
        queue.finish();
        flags.fetch_or(DF_LOOPBACK_TERMINAL, std::memory_order_release);
        SetEvent(ready.get()); SetEvent(terminal.get());
    }
    void run(std::wstring endpoint_id, DfLoopbackOptions options) noexcept {
        try {
            ComApartment com;
            AudioMmcssTask scheduling;
            require_setup(options.setup_cutoff_100ns);
            auto endpoint=exact_endpoint(endpoint_id.c_str());
            identity=require_corrected_driver(*endpoint.Get());
            if (!IsEqualGUID(identity.source_instance, options.source_instance) ||
                identity.build!=options.driver_build)
                throw std::runtime_error("owned endpoint identity changed");
            // Declared before audio so the event outlives every referencing COM stream.
            Handle audio_ready{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
            if (!audio_ready.get()) throw std::runtime_error("capture event creation failed");
            ComPtr<IAudioClient> audio;
            check(endpoint->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                reinterpret_cast<void**>(audio.GetAddressOf())), "owned loopback activation");
            WAVEFORMATEX* raw=nullptr;
            check(audio->GetMixFormat(&raw), "owned loopback format");
            std::unique_ptr<WAVEFORMATEX, decltype(&CoTaskMemFree)> format(raw, CoTaskMemFree);
            bool is_float=false;
            if (!format || !supported_mix(*format, is_float))
                throw std::runtime_error("owned loopback requires 48k stereo float32/PCM16");
            EnginePeriodCompanion companion;
            if (options.minimum_period) companion.initialize(*endpoint.Get(), *format,
                engine_periods(*audio.Get(), *format));
            check(audio->Initialize(AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                kBufferDuration, 0, format.get(), nullptr), "owned loopback Initialize");
            check(audio->SetEventHandle(audio_ready.get()), "owned loopback event");
            check(audio->GetBufferSize(&capacity_frames), "owned loopback capacity");
            ComPtr<IAudioCaptureClient> reader;
            check(audio->GetService(__uuidof(IAudioCaptureClient),
                reinterpret_cast<void**>(reader.GetAddressOf())), "owned capture service");
            bool audio_started=false;
            try {
                require_setup(options.setup_cutoff_100ns);
                companion.start();
                require_setup(options.setup_cutoff_100ns);
                check(audio->Start(), "owned loopback Start"); audio_started=true;
                const auto periods=engine_periods(*audio.Get(), *format);
                if (FAILED(periods.current_result) || !periods.current_format_matches || !periods.current ||
                    (options.minimum_period && periods.current!=companion.selected()))
                    throw std::runtime_error("owned loopback actual period unavailable/changed");
                period_frames=periods.current;
                const auto observed=require_corrected_driver(*endpoint.Get());
                if (!IsEqualGUID(observed.source_instance, identity.source_instance) || observed.build!=identity.build)
                    throw std::runtime_error("owned endpoint changed during setup");
                require_setup(options.setup_cutoff_100ns);
                flags.fetch_or(DF_LOOPBACK_READY, std::memory_order_release);
                SetEvent(ready.get());
                HANDLE waits[]={stop.get(), audio_ready.get(), companion.event()};
                while (!stopping()) {
                    const DWORD result=WaitForMultipleObjects(options.minimum_period?3:2, waits, FALSE, INFINITE);
                    if (result==WAIT_OBJECT_0) break;
                    if (result!=WAIT_OBJECT_0+1 && !(options.minimum_period && result==WAIT_OBJECT_0+2))
                        throw std::runtime_error("owned capture wait failed");
                    if (stopping()) break;
                    companion.service();
                    while (!stopping()) {
                        UINT32 available=0;
                        check(reader->GetNextPacketSize(&available), "owned next packet");
                        if (!available) break;
                        BYTE* data=nullptr; UINT32 frames=0; DWORD packet_flags=0;
                        UINT64 device_frame=0, qpc=0;
                        check(reader->GetBuffer(&data, &frames, &packet_flags, &device_frame, &qpc), "owned GetBuffer");
                        // Every successful GetBuffer is released, including exceptions/Stop/empty packets.
                        struct PacketRelease {
                            IAudioCaptureClient* reader; UINT32 frames; OwnedLoopback* owner;
                            bool pending=true;
                            HRESULT finish() noexcept {
                                pending=false; const auto hr=reader->ReleaseBuffer(frames);
                                if (FAILED(hr)) owner->fail(hr); return hr;
                            }
                            ~PacketRelease() { if (pending) finish(); }
                        } release{reader.Get(), frames, this};
                        if (device_frame>UINT64_MAX-frames || (!data && frames && !(packet_flags&AUDCLNT_BUFFERFLAGS_SILENT)))
                            throw std::runtime_error("invalid owned capture packet");
                        for (UINT32 offset=0; offset<frames && !stopping();) {
                            const UINT32 count=std::min<UINT32>(kMaxFrames, frames-offset);
                            const uint64_t delta=uint64_t(offset)*10'000'000/kSampleRate;
                            if (!(packet_flags&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) && qpc>UINT64_MAX-delta)
                                throw std::runtime_error("capture QPC overflow");
                            Block* slot=nullptr; uint64_t token=0;
                            if (queue.try_reserve(slot, token)) {
                                slot->first_device_frame=device_frame+offset;
                                slot->qpc_100ns=(packet_flags&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)?0:qpc+delta;
                                slot->flags=offset?packet_flags&~AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY:packet_flags;
                                slot->frame_count=count; slot->original_packet_offset_frames=offset;
                                convert(data, is_float, packet_flags, offset, *slot);
                                if (!queue.publish(token)) throw std::runtime_error("capture publication ownership changed");
                            } else {
                                if (queue.closed()) throw std::runtime_error("capture queue token exhausted");
                                dropped_positions.record(device_frame+offset,
                                    (packet_flags&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)?0:qpc+delta, count,
                                    offset?packet_flags&~AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY:packet_flags);
                            }
                            offset+=count;
                        }
                        check(release.finish(), "owned ReleaseBuffer");
                    }
                }
            } catch (...) { fail(E_FAIL); }
            // Never certify cleanup from logical Stop alone. These results survive thread exit.
            const HRESULT audio_stop=audio_started?audio->Stop():S_OK;
            const HRESULT companion_stop=companion.stop();
            if (FAILED(audio_stop) || FAILED(companion_stop)) {
                fail(FAILED(audio_stop)?audio_stop:companion_stop);
                flags.fetch_or(DF_LOOPBACK_STOP_UNCONFIRMED, std::memory_order_release);
            }
        } catch (...) { fail(E_FAIL); }
        // COM/MMCSS/stream destructors have completed on this same thread.
        try { dropped_positions.report(); } catch (...) { /* diagnostics never control custody */ }
        complete();
    }
};
}
extern "C" int deskfuse_open_owned_loopback(const DfLoopbackOptions* options, DfLoopbackOpen* output) noexcept {
    if (!options || !output || options->size!=sizeof(*options) || options->abi!=DF_LOOPBACK_ABI ||
        output->size!=sizeof(*output) || output->abi!=DF_LOOPBACK_ABI || !options->endpoint ||
        options->minimum_period>1 || options->reserved || !options->setup_cutoff_100ns ||
        options->driver_build!=DESKFUSE_TIMING_BUILD || IsEqualGUID(options->source_instance, GUID_NULL)) return -1;
    output->owner=nullptr; output->data=output->ready=output->terminal=nullptr;
    try {
        const size_t length=wcsnlen_s(options->endpoint, 1025);
        if (!length || length>1024) return -1;
        std::wstring endpoint(options->endpoint, length);
        auto owner=std::make_unique<OwnedLoopback>();
        const DfLoopbackOptions copied=*options;
        owner->require_setup(copied.setup_cutoff_100ns);
        owner->worker=std::thread([value=owner.get(), id=std::move(endpoint), copied]() mutable {
            value->run(std::move(id), copied);
        });
        output->owner=owner.get(); output->data=owner->queue.event();
        output->ready=owner->ready.get(); output->terminal=owner->terminal.get();
        owner.release(); return 0;
    } catch (...) { return -1; }
}
extern "C" int deskfuse_loopback_try_borrow(void* value, DfLoopbackRecord* output) noexcept {
    if (!value || !output || output->size!=sizeof(*output) || output->abi!=DF_LOOPBACK_ABI) return -1;
    auto& owner=*static_cast<OwnedLoopback*>(value);
    if (owner.queue.has_borrow()) return -1;
    const Block* block=nullptr; uint64_t token=0;
    if (!owner.queue.try_borrow(block, token)) return 0;
    *output={sizeof(*output), DF_LOOPBACK_ABI, token, block->first_device_frame, block->qpc_100ns,
        block->flags, block->frame_count, block->original_packet_offset_frames, block->frame_count*4, block->pcm.data()};
    return 1;
}
extern "C" int deskfuse_loopback_consume(void* value, uint64_t token) noexcept {
    return value && static_cast<OwnedLoopback*>(value)->queue.consume(token)?0:-1;
}
extern "C" int deskfuse_loopback_request_stop(void* value) noexcept {
    if (!value) return -1;
    auto& owner=*static_cast<OwnedLoopback*>(value);
    owner.flags.fetch_or(DF_LOOPBACK_STOP_REQUESTED, std::memory_order_release);
    return SetEvent(owner.stop.get())?0:-1;
}
extern "C" int deskfuse_loopback_join(void* value, uint32_t timeout_ms) noexcept {
    if (!value || timeout_ms==INFINITE) return -1;
    try {
        auto& owner=*static_cast<OwnedLoopback*>(value);
        if (!owner.joined) {
            const auto result=WaitForSingleObject(owner.worker.native_handle(), timeout_ms);
            if (result==WAIT_TIMEOUT) return 1;
            if (result!=WAIT_OBJECT_0) return -1;
            owner.worker.join(); owner.joined=true;
        }
        return owner.flags.load(std::memory_order_acquire)&DF_LOOPBACK_STOP_UNCONFIRMED?-1:0;
    } catch (...) { return -1; }
}
extern "C" int deskfuse_loopback_destroy(void* value) noexcept {
    if (!value) return -1;
    auto* owner=static_cast<OwnedLoopback*>(value);
    if (!owner->joined || owner->queue.has_borrow() ||
        (owner->flags.load(std::memory_order_acquire)&DF_LOOPBACK_STOP_UNCONFIRMED)) return -1;
    delete owner; return 0;
}
extern "C" int deskfuse_loopback_status(void* value, DfLoopbackStatus* output) noexcept {
    if (!value || !output || output->size!=sizeof(*output) || output->abi!=DF_LOOPBACK_ABI) return -1;
    auto& owner=*static_cast<OwnedLoopback*>(value);
    const auto flags=owner.flags.load(std::memory_order_acquire);
    *output={sizeof(*output), DF_LOOPBACK_ABI, flags | (owner.joined?DF_LOOPBACK_JOINED:0) |
        (owner.queue.has_borrow()?DF_LOOPBACK_BORROWED:0) | (owner.queue.has_records()?DF_LOOPBACK_HAS_RECORDS:0),
        owner.error.load(std::memory_order_relaxed), owner.queue.dropped(), 0, 0, {}, 0};
    if (flags&DF_LOOPBACK_READY) {
        output->period_frames=owner.period_frames; output->capacity_frames=owner.capacity_frames;
        output->source_instance=owner.identity.source_instance; output->driver_build=owner.identity.build;
    }
    return 0;
}
