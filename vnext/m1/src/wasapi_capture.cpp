// DeskFuse vNext M1 — native Windows 10+ WASAPI capture adapter.
//
// An endpoint-selectable EVENT-DRIVEN loopback source. No driver install,
// endpoint reroute, APK change, Rust/QUIC ownership, or synthetic send clock.
// Emits timestamped PCM16 48kHz stereo blocks with source device positions.
// A bounded eight-block writer queue drops on pressure: never stalls WASAPI
// buffer ownership to wait for a pipe/file/transport consumer.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <audioclient.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propvarutil.h>
#include <ksmedia.h>
#include <wrl/client.h>

#include "spsc_capture_queue.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <exception>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>

using Microsoft::WRL::ComPtr;

namespace {
constexpr uint32_t kSampleRate = 48'000;
constexpr uint32_t kChannels = 2;
constexpr uint32_t kMaxFrames = 240;
constexpr uint32_t kQueueSlots = 8;
constexpr uint32_t kPayloadLimit = kMaxFrames * kChannels * sizeof(int16_t);
constexpr DWORD kHeaderSize = 40;
constexpr REFERENCE_TIME kBufferDuration = 2'000'000; // 200 ms capacity, not pacing

void check(HRESULT hr, const char* action) {
    if (FAILED(hr)) {
        throw std::runtime_error(std::string(action) + " failed HRESULT=" +
                                 std::to_string(static_cast<uint32_t>(hr)));
    }
}

class ComApartment {
public:
    ComApartment() { check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "CoInitializeEx"); }
    ~ComApartment() { CoUninitialize(); }
};

// Best-effort MMCSS Audio registration for this exact event-driven capture
// thread. The pipe writer remains ordinary priority; slow I/O must never
// inherit real-time scheduling. Lack of MMCSS cannot prevent audible output.
class AudioMmcssTask {
public:
    AudioMmcssTask() {
        DWORD task_index = 0;
        task_ = AvSetMmThreadCharacteristicsW(L"Audio", &task_index);
        if (!task_) {
            std::cerr << "WARN WASAPI MMCSS registration unavailable; "
                      << "ordinary thread priority error=" << GetLastError() << "\n";
        }
    }
    ~AudioMmcssTask() {
        if (task_) AvRevertMmThreadCharacteristics(task_);
    }
    AudioMmcssTask(const AudioMmcssTask&) = delete;
    AudioMmcssTask& operator=(const AudioMmcssTask&) = delete;
private:
    HANDLE task_ = nullptr;
};

class Handle {
public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() {
        if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_);
    }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    [[nodiscard]] HANDLE get() const { return value_; }
    void reset(HANDLE v) {
        if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_);
        value_ = v;
    }
private:
    HANDLE value_;
};

struct Block {
    uint64_t first_device_frame = 0;
    uint64_t qpc_100ns = 0;
    uint32_t flags = 0;
    uint32_t frame_count = 0;
    uint32_t original_packet_offset_frames = 0;
    std::array<uint8_t, kPayloadLimit> pcm{};
};


// Only the capture producer touches this fixed storage. No I/O, allocation,
// mutex or wait on packet drops. Preserve the first and latest eight source
// positions so an overflow can still be localized AFTER WASAPI stops.
class NativeDropLedger {
    struct Entry {
        uint64_t frame = 0, qpc = 0;
        uint32_t frames = 0, flags = 0;
    };
public:
    void record(const Block& b) noexcept {
        const uint64_t ordinal = ++count_;
        const size_t slot = ordinal <= 8
            ? static_cast<size_t>(ordinal - 1)
            : 8 + static_cast<size_t>((ordinal - 9) % 8);
        entries_[slot] = {b.first_device_frame, b.qpc_100ns,
                          b.frame_count, b.flags};
    }
    void report() const {
        auto emit = [&](size_t slot, uint64_t ordinal) {
            const auto& b = entries_[slot];
            std::cerr << "TRACE Windows WASAPI_NATIVE_WRITER_DROP"
                      << " frame=" << b.frame << " frames=" << b.frames
                      << " qpc100=" << b.qpc << " flags=" << b.flags
                      << " count=" << ordinal << "\n";
        };
        const uint64_t first = std::min<uint64_t>(count_, 8);
        for (uint64_t n = 1; n <= first; ++n)
            emit(static_cast<size_t>(n - 1), n);
        const uint64_t recent = std::min<uint64_t>(count_ - first, 8);
        for (uint64_t n = count_ - recent + 1; n <= count_; ++n)
            emit(8 + static_cast<size_t>((n - 9) % 8), n);
    }
private:
    std::array<Entry, 16> entries_{};
    uint64_t count_ = 0;
};

void write_all(HANDLE h, const uint8_t* data, size_t size,
               const std::atomic<bool>* cancelled = nullptr) {
    while (size != 0) {
        if (cancelled && cancelled->load(std::memory_order_acquire)) {
            throw std::runtime_error("capture output cancelled during shutdown");
        }
        DWORD written = 0;
        const DWORD request = static_cast<DWORD>(std::min<size_t>(size, 65536));
        if (!WriteFile(h, data, request, &written, nullptr) || written == 0) {
            throw std::runtime_error("capture output WriteFile failed or pipe closed");
        }
        data += written;
        size -= written;
    }
}

void put32(uint8_t* at, uint32_t v) {
    for (int i = 0; i < 4; ++i) at[i] = static_cast<uint8_t>(v >> (8 * i));
}
void put64(uint8_t* at, uint64_t v) {
    for (int i = 0; i < 8; ++i) at[i] = static_cast<uint8_t>(v >> (8 * i));
}

// 40-byte little-endian narrow IPC header, followed by 1..240 stereo PCM16
// frames. No authentication: pipe/file is local. QUIC transport remains Rust-owned.
// DFWC v2 preserves its original fixed 40-byte local header size.
// Offset: magic[4], version[4], first_device_frame[8], qpc_100ns[8],
//         frame_count[4], flags[4], payload_bytes[4],
//         original_GetBuffer_packet_offset_frames[4].
// This offset is provenance, not an audio-clock offset or a send timer.
// A v1 reader MUST reject this wire version rather than misread the field.
void write_block(HANDLE out, const Block& b,
                 const std::atomic<bool>* cancelled = nullptr) {
    if (b.frame_count == 0 || b.frame_count > kMaxFrames) {
        throw std::runtime_error("invalid WASAPI writer frame count");
    }
    // The DFWC header and PCM payload form one contiguous pipe record.
    // One WriteFile per 5-ms block avoids a second syscall and pipe wake.
    // This runs on the writer thread, never on the WASAPI capture callback.
    std::array<uint8_t, kHeaderSize + kPayloadLimit> record{};
    uint8_t* header = record.data();
    header[0] = 'D'; header[1] = 'F'; header[2] = 'W'; header[3] = 'C';
    put32(header + 4, 2);
    put64(header + 8, b.first_device_frame);
    put64(header + 16, b.qpc_100ns);
    put32(header + 24, b.frame_count);
    put32(header + 28, b.flags);
    const size_t pcm_bytes = static_cast<size_t>(b.frame_count) *
                             kChannels * sizeof(int16_t);
    put32(header + 32, static_cast<uint32_t>(pcm_bytes));
    put32(header + 36, b.original_packet_offset_frames);
    std::memcpy(record.data() + kHeaderSize, b.pcm.data(), pcm_bytes);
    write_all(out, record.data(), kHeaderSize + pcm_bytes, cancelled);
}

// Owns the writer until completion, including when WASAPI Start/Stop fails.
// Only shutdown waits: normal capture never waits for this thread or pipe I/O.
// Permit a short final drain, then cancel synchronous I/O. Retrying cancellation
// closes the race where the writer starts WriteFile just after our first cancel.
// This is cooperative OS cancellation, not a scheduling-time guarantee.
class CaptureWriter {
public:
    using Queue = df_wasapi::SpscCaptureQueue<Block, kQueueSlots>;
    CaptureWriter(Queue& queue, HANDLE out) : queue_(queue), thread_([this, out] {
        try {
            Block b;
            while (!cancelled_.load(std::memory_order_acquire) && queue_.pop(b)) {
                write_block(out, b, &cancelled_);
            }
        } catch (...) {
            error_ = std::current_exception();
            failed_.store(true, std::memory_order_release);
        }
    }) {}
    ~CaptureWriter() { finish(); }
    CaptureWriter(const CaptureWriter&) = delete;
    CaptureWriter& operator=(const CaptureWriter&) = delete;

    bool failed() const noexcept { return failed_.load(std::memory_order_acquire); }
    bool cancelled() const noexcept { return cancelled_.load(std::memory_order_acquire); }
    void finish() noexcept {
        if (!thread_.joinable()) return;
        queue_.finish();
        const HANDLE thread = thread_.native_handle();
        if (WaitForSingleObject(thread, 250) != WAIT_OBJECT_0) {
            cancelled_.store(true, std::memory_order_release);
            do {
                // ERROR_NOT_FOUND is harmless if no synchronous I/O was pending.
                CancelSynchronousIo(thread);
            } while (WaitForSingleObject(thread, 10) != WAIT_OBJECT_0);
        }
        thread_.join();
    }
    void check_result() const {
        // Called only after finish/join, which synchronizes error_ ownership.
        if (error_) std::rethrow_exception(error_);
        if (cancelled()) throw std::runtime_error("capture output drain exceeded shutdown limit");
    }
private:
    Queue& queue_;
    std::atomic<bool> cancelled_{false}, failed_{false};
    std::exception_ptr error_;
    std::thread thread_;
};


struct PacketObservation {
    uint64_t source_frame = 0, packet_qpc100 = 0, local_qpc100 = 0;
    uint32_t frames = 0, flags = 0;
};
struct PacketTimingTrace {
    std::wstring path;
    LARGE_INTEGER frequency{};
    // Fixed 160-KiB budget, sufficient for every 480-frame original
    // GetBuffer packet during the short <=20s silent diagnostic. No
    // allocation, lock, log write or wait on the capture thread.
    std::unique_ptr<std::array<PacketObservation, 4096>> samples;
    size_t recorded = 0;
    uint64_t eligible = 0;

    PacketTimingTrace() {
        wchar_t value[2048]{};
        DWORD n = GetEnvironmentVariableW(L"DESKFUSE_WASAPI_PACKET_TRACE",
                                           value, DWORD(std::size(value)));
        if (!n) return;
        if (n >= std::size(value)) throw std::runtime_error("packet trace path too long");
        path.assign(value, value + n);
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0)
            throw std::runtime_error("packet trace QPC frequency unavailable");
        // Allocate the fixed diagnostic budget ONCE at startup, only for an
        // explicit silent trace. The production capture path allocates none.
        samples = std::make_unique<std::array<PacketObservation, 4096>>();
    }
    void observe(UINT64 frame, UINT32 frames, UINT64 first_qpc100, DWORD flags) {
        if (path.empty() || frames < 480) return;
        ++eligible;
        // No disk I/O, allocations, locks or waits on WASAPI capture
        // thread. An exhaustive bounded trace prevents independently
        // sampled native/sender diagnostics from missing the same packet.
        if (recorded == samples->size()) return;
        LARGE_INTEGER now{};
        if (!QueryPerformanceCounter(&now) || now.QuadPart < 0) return;
        const uint64_t ticks = uint64_t(now.QuadPart);
        const uint64_t hz = uint64_t(frequency.QuadPart);
        const uint64_t current = ticks / hz * 10'000'000ULL +
                                 (ticks % hz) * 10'000'000ULL / hz;
        (*samples)[recorded++] = {frame, first_qpc100, current, frames, flags};
    }
    void save() const {
        if (path.empty()) return;
        Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                                nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (file.get() == INVALID_HANDLE_VALUE)
            throw std::runtime_error("cannot create original-packet timing CSV");
        const std::string header =
            "first_frame,packet_frames,flags,packet_qpc100,local_getbuffer_qpc100,"
            "first_fragment_frame,first_fragment_qpc100,"
            "second_fragment_frame,second_fragment_qpc100,"
            "first_future_100ns,second_future_100ns\n";
        write_all(file.get(), reinterpret_cast<const uint8_t*>(header.data()), header.size());
        for (size_t i = 0; i < recorded; ++i) {
            const auto& p = (*samples)[i];
            const uint64_t later = p.packet_qpc100 == 0 ? 0 :
                                   p.packet_qpc100 + 240ULL * 10'000'000ULL / kSampleRate;
            const auto first_future = int64_t(p.packet_qpc100) - int64_t(p.local_qpc100);
            const auto second_future = int64_t(later) - int64_t(p.local_qpc100);
            const std::string row =
                std::to_string(p.source_frame) + "," +
                std::to_string(p.frames) + "," + std::to_string(p.flags) + "," +
                std::to_string(p.packet_qpc100) + "," +
                std::to_string(p.local_qpc100) + "," +
                std::to_string(p.source_frame) + "," +
                std::to_string(p.packet_qpc100) + "," +
                std::to_string(p.source_frame + 240) + "," +
                std::to_string(later) + "," +
                std::to_string(first_future) + "," +
                std::to_string(second_future) + "\n";
            write_all(file.get(), reinterpret_cast<const uint8_t*>(row.data()), row.size());
        }
    }
};

bool supported_mix(const WAVEFORMATEX& wf, bool& is_float) {
    if (wf.nSamplesPerSec != kSampleRate || wf.nChannels != kChannels) return false;
    const bool extensible = wf.wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        wf.cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    if (extensible) {
        const auto& ex = reinterpret_cast<const WAVEFORMATEXTENSIBLE&>(wf);
        is_float = IsEqualGUID(ex.SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) &&
                   wf.wBitsPerSample == 32 && ex.Samples.wValidBitsPerSample == 32;
        if (is_float && wf.nBlockAlign == 8) return true;
        is_float = false;
        return IsEqualGUID(ex.SubFormat, KSDATAFORMAT_SUBTYPE_PCM) &&
               wf.wBitsPerSample == 16 && ex.Samples.wValidBitsPerSample == 16 &&
               wf.nBlockAlign == 4;
    }
    is_float = wf.wFormatTag == WAVE_FORMAT_IEEE_FLOAT && wf.wBitsPerSample == 32 &&
               wf.nBlockAlign == 8;
    return is_float || (wf.wFormatTag == WAVE_FORMAT_PCM &&
                        wf.wBitsPerSample == 16 && wf.nBlockAlign == 4);
}

int16_t float_to_pcm(float sample) {
    if (!std::isfinite(sample)) return 0;
    const float clamped = std::clamp(sample, -1.0f, 1.0f);
    return static_cast<int16_t>(std::lrintf(clamped * 32767.0f));
}

void convert(const uint8_t* data, bool is_float, DWORD flags,
             UINT32 frame_offset, Block& b) {
    if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
        std::fill(b.pcm.begin(), b.pcm.begin() + b.frame_count * 4, 0);
        return;
    }
    const uint8_t* src = data + static_cast<size_t>(frame_offset) * (is_float ? 8 : 4);
    for (uint32_t i = 0; i < b.frame_count * kChannels; ++i) {
        int16_t pcm;
        if (is_float) {
            float f = 0;
            std::memcpy(&f, src + i * sizeof(float), sizeof(float));
            pcm = float_to_pcm(f);
        } else {
            std::memcpy(&pcm, src + i * sizeof(int16_t), sizeof(int16_t));
        }
        const uint16_t bits = static_cast<uint16_t>(pcm);
        b.pcm[2 * i] = static_cast<uint8_t>(bits);
        b.pcm[2 * i + 1] = static_cast<uint8_t>(bits >> 8);
    }
}

void enumerate(IMMDeviceEnumerator& e) {
    ComPtr<IMMDeviceCollection> devices;
    check(e.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices), "EnumAudioEndpoints");
    UINT count = 0;
    check(devices->GetCount(&count), "GetCount");
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> dev;
        check(devices->Item(i, &dev), "IMMDeviceCollection::Item");
        LPWSTR id = nullptr;
        check(dev->GetId(&id), "IMMDevice::GetId");
        ComPtr<IPropertyStore> store;
        std::wstring name = L"(unnamed render endpoint)";
        if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &store))) {
            PROPVARIANT value;
            PropVariantInit(&value);
            if (SUCCEEDED(store->GetValue(PKEY_Device_FriendlyName, &value)) &&
                value.vt == VT_LPWSTR && value.pwszVal) {
                name = value.pwszVal;
            }
            PropVariantClear(&value);
        }
        std::wcout << L"ENDPOINT " << name << L"\nID " << id << L"\n";
        CoTaskMemFree(id);
    }
}

void capture(IMMDeviceEnumerator& enumerator, const std::wstring& device_id,
             HANDLE out, uint32_t seconds) {
    ComPtr<IMMDevice> dev;
    if (device_id.empty()) {
        check(enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, &dev),
              "GetDefaultAudioEndpoint");
    } else {
        check(enumerator.GetDevice(device_id.c_str(), &dev), "GetDevice");
    }

    ComPtr<IAudioClient> audio;
    check(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                       reinterpret_cast<void**>(audio.GetAddressOf())), "Activate IAudioClient");
    WAVEFORMATEX* raw = nullptr;
    check(audio->GetMixFormat(&raw), "GetMixFormat");
    std::unique_ptr<WAVEFORMATEX, decltype(&CoTaskMemFree)> format(raw, CoTaskMemFree);
    bool is_float = false;
    if (!supported_mix(*format, is_float)) {
        throw std::runtime_error("Selected endpoint mix format is not 48kHz stereo "
                                 "float32/PCM16. A source-rate/channel adapter is "
                                 "required; refusing to mislabel frames.");
    }

    check(audio->Initialize(AUDCLNT_SHAREMODE_SHARED,
                            AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                            kBufferDuration, 0, format.get(), nullptr), "IAudioClient::Initialize");
    Handle ready(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (!ready.get()) throw std::runtime_error("CreateEvent failed");
    check(audio->SetEventHandle(ready.get()), "SetEventHandle");
    ComPtr<IAudioCaptureClient> reader;
    check(audio->GetService(__uuidof(IAudioCaptureClient),
                            reinterpret_cast<void**>(reader.GetAddressOf())), "GetService capture");

    PacketTimingTrace packet_trace;
    df_wasapi::SpscCaptureQueue<Block, kQueueSlots> queue;
    CaptureWriter writer(queue, out);
    NativeDropLedger native_drops;
    uint64_t observed = 0;
    const auto start = std::chrono::steady_clock::now();
    std::cerr << "WASAPI capturing selected render endpoint, 48k stereo PCM16; "
              << "queue_slots=" << kQueueSlots << " duration=" << seconds << "s\n";
    AudioMmcssTask mmcss;
    HRESULT start_result = audio->Start();
    if (FAILED(start_result)) {
        writer.finish();
        check(start_result, "IAudioClient::Start");
    }
    try {
        while (std::chrono::steady_clock::now() - start < std::chrono::seconds(seconds)) {
            if (writer.failed()) throw std::runtime_error("binary output writer failed");
            const DWORD status = WaitForSingleObject(ready.get(), 250);
            if (status == WAIT_FAILED) throw std::runtime_error("WaitForSingleObject failed");
            if (status != WAIT_OBJECT_0) continue; // idle audio is not synthetic silence
            for (;;) {
                UINT32 available = 0;
                check(reader->GetNextPacketSize(&available), "GetNextPacketSize");
                if (!available) break;
                BYTE* raw_data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                UINT64 device_pos = 0, qpc100 = 0;
                const HRESULT get = reader->GetBuffer(&raw_data, &frames, &flags,
                                                       &device_pos, &qpc100);
                check(get, "IAudioCaptureClient::GetBuffer");
                if (!frames) {
                    // Every successful GetBuffer must be paired with
                    // ReleaseBuffer, including an empty capture fragment.
                    check(reader->ReleaseBuffer(0), "IAudioCaptureClient::ReleaseBuffer(0)");
                    continue;
                }
                packet_trace.observe(device_pos, frames,
                    (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) ? 0 : qpc100, flags);
                for (UINT32 offset = 0; offset < frames; offset += kMaxFrames) {
                    Block b;
                    b.first_device_frame = device_pos + offset;
                    b.qpc_100ns = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)
                        ? 0 : qpc100 + static_cast<uint64_t>(offset) * 10'000'000 / kSampleRate;
                    // Engine discontinuity belongs to the first fragment of
                    // this GetBuffer packet, not every 240-frame slice.
                    b.flags = offset == 0 ? flags : (flags & ~AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY);
                    b.frame_count = std::min<UINT32>(kMaxFrames, frames - offset);
                    // Preserve original WASAPI GetBuffer membership. This
                    // lets Rust check the packet's first-frame QPC rather than
                    // treating a 5-ms derived fragment as independent input.
                    b.original_packet_offset_frames = offset;
                    convert(raw_data, is_float, flags, offset, b);
                    if (!queue.try_push(b)) native_drops.record(b);
                    observed += b.frame_count;
                }
                check(reader->ReleaseBuffer(frames), "IAudioCaptureClient::ReleaseBuffer");
            }
        }
    } catch (...) {
        audio->Stop();
        writer.finish();
        native_drops.report();
        std::cerr << "EARLY WASAPI capture source_frames_observed=" << observed
                  << " dropped_blocks_bounded_writer=" << queue.dropped()
                  << " writer_failed=" << writer.failed() << "\n";
        try { writer.check_result(); }
        catch (const std::exception& e) { std::cerr << "FAIL WASAPI writer: " << e.what() << "\n"; }
        try { packet_trace.save(); }
        catch (const std::exception& e) {
            std::cerr << "FAIL WASAPI original-packet trace: " << e.what() << "\n";
        }
        throw;
    }
    const HRESULT stop_result = audio->Stop();
    writer.finish();
    check(stop_result, "IAudioClient::Stop");
    native_drops.report();
    packet_trace.save();
    writer.check_result();
    std::cerr << "DONE WASAPI capture source_frames_observed=" << observed
              << " dropped_blocks_bounded_writer=" << queue.dropped() << "\n";
}

} // namespace

#ifndef DESKFUSE_CAPTURE_LIFECYCLE_TEST
int wmain(int argc, wchar_t** argv) {
    try {
        bool list = false;
        std::wstring device_id, path;
        uint32_t seconds = 10;
        for (int i = 1; i < argc; ++i) {
            const std::wstring arg = argv[i];
            if (arg == L"--list") list = true;
            else if (arg == L"--device-id" && i + 1 < argc) device_id = argv[++i];
            else if (arg == L"--out" && i + 1 < argc) path = argv[++i];
            else if (arg == L"--seconds" && i + 1 < argc) {
                const unsigned long n = std::stoul(argv[++i]);
                if (n < 1 || n > 1800) throw std::runtime_error("seconds must be 1..1800");
                seconds = static_cast<uint32_t>(n);
            } else {
                throw std::runtime_error("usage: deskfuse-wasapi-capture --list | "
                                         "[--device-id <render-endpoint-id>] --out <file|-> "
                                         "[--seconds 1..60]");
            }
        }
        ComApartment com;
        ComPtr<IMMDeviceEnumerator> enumerator;
        check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                               IID_PPV_ARGS(&enumerator)), "CoCreateInstance");
        if (list) {
            enumerate(*enumerator.Get());
            return 0;
        }
        if (path.empty()) throw std::runtime_error("explicit --out <file|-> required");
        Handle file;
        HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
        if (path != L"-") {
            out = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                              CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (out == INVALID_HANDLE_VALUE) {
                throw std::runtime_error("refusing overwrite or cannot create capture file");
            }
            file.reset(out);
        }
        if (!out || out == INVALID_HANDLE_VALUE) throw std::runtime_error("invalid output handle");
        capture(*enumerator.Get(), device_id, out, seconds);
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "FAIL WASAPI capture: " << e.what() << "\n";
        return 1;
    }
}
#endif
