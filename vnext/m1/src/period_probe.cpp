// Local, phone-free WaveRT stream lifetime experiment. No media/network path.
#define DESKFUSE_CAPTURE_LIFECYCLE_TEST
#include "wasapi_capture.cpp"
#include <winioctl.h>
#include "../../../windows/driver/DeskFuseAudio/src/pcm_export_abi.h"

namespace {
ComPtr<IMMDevice> exact_endpoint(const wchar_t* id) {
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)), "enumerator");
    ComPtr<IMMDevice> endpoint;
    check(enumerator->GetDevice(id, &endpoint), "exact source endpoint");
    return endpoint;
}
class SilentRender {
public:
    void open(IMMDevice& endpoint, const WAVEFORMATEX& format, UINT32 frames,
              bool ordinaryAppTone = false) {
        tone_ = ordinaryAppTone;
        if (tone_ && !supported_mix(format, floatMix_))
            throw std::runtime_error("ordinary test app requires 48k stereo float32/PCM16 mix");
        check(endpoint.Activate(__uuidof(IAudioClient3), CLSCTX_ALL, nullptr,
            reinterpret_cast<void**>(audio_.GetAddressOf())), "render Activate");
        if (tone_) {
            // Typical shared-mode app: adopt the running engine period; do NOT
            // explicitly request a new 480-frame engine.
            check(audio_->Initialize(AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, &format, nullptr),
                "ordinary app Initialize");
        } else {
            AudioClientProperties properties{};
            properties.cbSize = sizeof(properties);
            properties.eCategory = AudioCategory_Other;
            properties.Options = AUDCLNT_STREAMOPTIONS_MATCH_FORMAT;
            check(audio_->SetClientProperties(&properties), "normal SetClientProperties");
            check(audio_->InitializeSharedAudioStream(AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                frames, &format, nullptr), "normal InitializeSharedAudioStream");
        }
        event_.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        if (!event_.get()) throw std::runtime_error("normal CreateEvent failed");
        check(audio_->SetEventHandle(event_.get()), "normal SetEventHandle");
        check(audio_->GetBufferSize(&capacity_), "normal GetBufferSize");
        if (!capacity_) throw std::runtime_error("normal capacity is zero");
        check(audio_->GetService(__uuidof(IAudioRenderClient),
            reinterpret_cast<void**>(render_.GetAddressOf())), "normal GetService");
        service();
        check(audio_->Start(), "normal Start");
        running_ = true;
    }
    void service() {
        UINT32 padding = 0;
        check(audio_->GetCurrentPadding(&padding), "normal GetCurrentPadding");
        if (padding > capacity_) throw std::runtime_error("normal padding invalid");
        const UINT32 count = capacity_ - padding;
        if (!count) return;
        BYTE* data = nullptr;
        check(render_->GetBuffer(count, &data), "render GetBuffer");
        if (tone_) {
            // 250 Hz low-level square wave, only on the throwaway test endpoint.
            for (UINT32 i = 0; i < count; ++i, ++toneFrame_) {
                const float sample = ((toneFrame_ / 96) & 1) ? 0.08f : -0.08f;
                for (UINT32 ch = 0; ch < 2; ++ch) {
                    if (floatMix_) {
                        std::memcpy(data + (i * 2 + ch) * 4, &sample, sizeof(sample));
                    } else {
                        const int16_t pcm = sample > 0 ? 2621 : -2621;
                        std::memcpy(data + (i * 2 + ch) * 2, &pcm, sizeof(pcm));
                    }
                }
            }
        }
        check(render_->ReleaseBuffer(count, tone_ ? 0 : AUDCLNT_BUFFERFLAGS_SILENT),
            "render ReleaseBuffer");
    }
    [[nodiscard]] HANDLE event() const { return event_.get(); }
    void close() noexcept {
        if (running_) { audio_->Stop(); running_ = false; }
        render_.Reset();
        audio_.Reset();
        event_.reset(nullptr);
    }
    ~SilentRender() { close(); }
private:
    Handle event_; // release after audio client
    ComPtr<IAudioClient3> audio_;
    ComPtr<IAudioRenderClient> render_;
    UINT32 capacity_ = 0;
    uint64_t toneFrame_ = 0;
    bool running_ = false, tone_ = false, floatMix_ = false;
};

void pump(SilentRender* normal, EnginePeriodCompanion* fast, DWORD milliseconds = 900) {
    HANDLE events[2]{};
    DWORD count = 0;
    if (normal) events[count++] = normal->event();
    if (fast) events[count++] = fast->event();
    if (!count) return;
    const auto until = GetTickCount64() + milliseconds;
    while (GetTickCount64() < until) {
        DWORD result = WaitForMultipleObjects(count, events, FALSE, 100);
        if (result == WAIT_TIMEOUT) continue;
        if (result == WAIT_FAILED || result < WAIT_OBJECT_0 || result >= WAIT_OBJECT_0 + count)
            throw std::runtime_error("render event wait failed");
        try {
            if (normal && result == WAIT_OBJECT_0) normal->service();
            else if (fast) fast->service();
        } catch (const std::exception& error) {
            std::cerr << "SERVICE_ERROR " << error.what() << "\n";
            return;
        }
    }
}

void report_period(IAudioClient& query, const WAVEFORMATEX& format, const char* phase) {
    const auto p = engine_periods(query, format);
    std::cout << "PERIOD phase=" << phase << " t_ms=" << GetTickCount64()
              << " normal=" << p.normal << " minimum=" << p.minimum
              << " current=" << p.current
              << " current_hr=" << static_cast<uint32_t>(p.current_result) << "\n";
}

void bind_snapshot(const char* phase, const GUID& source,
                   SilentRender* app = nullptr, EnginePeriodCompanion* fast = nullptr) {
    Handle file(CreateFileW(LR"(\\.\DeskFusePcm)", GENERIC_READ | GENERIC_WRITE, 0,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (file.get() == INVALID_HANDLE_VALUE)
        throw std::runtime_error("DeskFusePcm CreateFile failed: " + std::to_string(GetLastError()));
    Handle process(OpenProcess(PROCESS_DUP_HANDLE, FALSE, GetCurrentProcessId()));
    if (!process.get()) throw std::runtime_error("OpenProcess self failed");

    DeskFusePcmBind request{sizeof(DeskFusePcmBind), DESKFUSE_PCM_ABI, source,
        reinterpret_cast<unsigned long long>(process.get())};
    DeskFusePcmHandles handles{};
    DWORD written = 0;
    const BOOL bound = DeviceIoControl(file.get(), DESKFUSE_PCM_BIND,
        &request, sizeof(request), &handles, sizeof(handles), &written, nullptr);
    const DWORD bind_error = bound ? 0 : GetLastError();
    std::cout << "BIND phase=" << phase << " t_ms=" << GetTickCount64()
              << " result=" << (bound ? "success" : "failure")
              << " win32=" << bind_error << "\n";
    DeskFusePcmBindDiagnostic diagnostic{};
    DWORD returned = 0;
    if (DeviceIoControl(file.get(), DESKFUSE_PCM_LAST_BIND_DIAGNOSTIC, nullptr, 0,
        &diagnostic, sizeof(diagnostic), &returned, nullptr) &&
        returned == sizeof(diagnostic) && diagnostic.abi == 1) {
        const auto& g = diagnostic.geometry;
        std::cout << "SNAPSHOT phase=" << phase << " status=" << diagnostic.status
                  << " streams=" << diagnostic.streams << " selected=" << diagnostic.selected
                  << " denied=" << diagnostic.firstDenial << " flags=" << diagnostic.flags
                  << " geometry=" << g.state << "/" << g.packetFrames << "/"
                  << g.notifications << "/" << g.sampleRate << "\n";
        for (unsigned i = 0; i < diagnostic.count && i < 8; ++i) {
            const auto& e = diagnostic.events[(diagnostic.next + 8 - diagnostic.count + i) % 8];
            std::cout << "EVENT phase=" << phase << " seq=" << e.sequence
                      << " kind=" << e.kind << " stream=" << e.serial
                      << " open=" << e.streams << " flags=" << e.flags
                      << " frames=" << e.geometry.packetFrames
                      << " notifications=" << e.geometry.notifications
                      << " rate=" << e.geometry.sampleRate << "\n";
        }
    } else {
        std::cout << "SNAPSHOT phase=" << phase << " unavailable win32=" << GetLastError() << "\n";
    }
    if (bound) {
        if (written != sizeof(handles) || handles.abi != DESKFUSE_PCM_ABI)
            throw std::runtime_error("PCM bind returned invalid handles");
        if (app && fast) {
            const auto view = static_cast<const DeskFusePcmMapping*>(MapViewOfFile(
                reinterpret_cast<HANDLE>(handles.section), FILE_MAP_READ, 0, 0,
                sizeof(DeskFusePcmMapping)));
            if (view) {
                // Bind AFTER the app is producing, then consume only summary data.
                // No ACK is required: eight slots suffice to verify provenance.
                pump(app, fast, 150);
                unsigned slots = 0;
                uint64_t nonzero = 0, samples = 0;
                for (const auto& slot : view->slots) {
                    if (slot.ticket == 0 || slot.frames == 0 || slot.frames > DESKFUSE_PCM_FRAMES)
                        continue;
                    ++slots;
                    const size_t bytes = static_cast<size_t>(slot.frames) *
                        DESKFUSE_PCM_BYTES_PER_FRAME;
                    for (size_t i = 0; i < bytes; ++i) {
                        nonzero += slot.pcm[i] != 0;
                        ++samples;
                    }
                }
                std::cout << "PCM_SAMPLE phase=" << phase
                          << " state=" << view->control.state
                          << " published=" << view->control.published
                          << " slots=" << slots << " bytes=" << samples
                          << " nonzero_bytes=" << nonzero << "\n";
                UnmapViewOfFile(view);
            } else {
                std::cout << "PCM_SAMPLE phase=" << phase
                          << " map_failed=" << GetLastError() << "\n";
            }
        }
        CloseHandle(reinterpret_cast<HANDLE>(handles.section));
        CloseHandle(reinterpret_cast<HANDLE>(handles.event));
        DWORD ignored = 0;
        if (!DeviceIoControl(file.get(), DESKFUSE_PCM_REVOKE, nullptr, 0,
            nullptr, 0, &ignored, nullptr))
            throw std::runtime_error("PCM revoke failed: " + std::to_string(GetLastError()));
    }
    std::cout << std::flush;
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        const bool fastFirst = argc == 4 && std::wstring(argv[3]) == L"--fast-first";
        if ((argc != 3 && !fastFirst) ||
            std::wstring(argv[1]) != L"--endpoint-id" || !argv[2][0])
            throw std::runtime_error("usage: deskfuse-period-probe --endpoint-id <temporary DeskFuse endpoint ID> [--fast-first]");
        ComApartment com;
        AudioMmcssTask mmcss;
        auto endpoint = exact_endpoint(argv[2]);
        const auto identity = require_corrected_driver(*endpoint.Get());
        if (identity.export_abi != DESKFUSE_PCM_ABI)
            throw std::runtime_error("installed driver does not support PCM export ABI 2");
        ComPtr<IAudioClient> query;
        check(endpoint->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
            reinterpret_cast<void**>(query.GetAddressOf())), "query Activate");
        WAVEFORMATEX* raw = nullptr;
        check(query->GetMixFormat(&raw), "query GetMixFormat");
        std::unique_ptr<WAVEFORMATEX, decltype(&CoTaskMemFree)> format(raw, CoTaskMemFree);
        const auto baseline = engine_periods(*query.Get(), *format);
        if (FAILED(baseline.legal_result) || !baseline.normal || !baseline.minimum)
            throw std::runtime_error("normal/minimum WASAPI engine periods unavailable");
        std::cout << "FORMAT rate=" << format->nSamplesPerSec << " channels=" << format->nChannels
                  << " bits=" << format->wBitsPerSample << "\n";
        report_period(*query.Get(), *format, "idle");
        if (fastFirst) {
            EnginePeriodCompanion fast;
            fast.initialize(*endpoint.Get(), *format, baseline);
            fast.start();
            pump(nullptr, &fast);
            report_period(*query.Get(), *format, "fast_first_alone");
            bind_snapshot("fast_first_alone", identity.source_instance);
            SilentRender app;
            app.open(*endpoint.Get(), *format, baseline.normal, true);
            pump(&app, &fast);
            report_period(*query.Get(), *format, "fast_first_with_app");
            bind_snapshot("fast_first_with_app", identity.source_instance, &app, &fast);
            app.close();
            check(fast.stop(), "fast-first Stop");
            std::cout << "DONE fast-first\n";
            return 0;
        }
        bind_snapshot("idle", identity.source_instance);

        SilentRender normal;
        normal.open(*endpoint.Get(), *format, baseline.normal);
        pump(&normal, nullptr);
        report_period(*query.Get(), *format, "normal");
        bind_snapshot("normal", identity.source_instance);

        {
            EnginePeriodCompanion fast;
            fast.initialize(*endpoint.Get(), *format, engine_periods(*query.Get(), *format));
            fast.start();
            pump(&normal, &fast);
            report_period(*query.Get(), *format, "overlap");
            bind_snapshot("overlap", identity.source_instance);

            normal.close();
            pump(nullptr, &fast);
            report_period(*query.Get(), *format, "fast_after_normal_closed");
            bind_snapshot("fast_after_normal_closed", identity.source_instance);
            check(fast.stop(), "fast Stop");
        }
        report_period(*query.Get(), *format, "all_closed");
        {
            EnginePeriodCompanion fast_alone;
            fast_alone.initialize(*endpoint.Get(), *format, engine_periods(*query.Get(), *format));
            fast_alone.start();
            pump(nullptr, &fast_alone);
            report_period(*query.Get(), *format, "fast_alone");
            bind_snapshot("fast_alone", identity.source_instance);
            check(fast_alone.stop(), "fast-alone Stop");
        }
        std::cout << "DONE\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "PROBE_FAILED " << error.what() << "\n";
        return 2;
    }
}
