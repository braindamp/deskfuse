// Control only: no PCM pipe, reader queue or network timer. WASAPI services
// are created, serviced and released on the SAME native period-owner thread.
#define DESKFUSE_CAPTURE_LIFECYCLE_TEST
#include "wasapi_capture.cpp"
#include "../../../windows/driver/DeskFuseAudio/src/pcm_export_abi.h"
namespace {
ComPtr<IMMDevice> exact_endpoint(const wchar_t* id) {
    if (!id || !*id) throw std::runtime_error("explicit endpoint required");
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator)),"enumerator");
    ComPtr<IMMDevice> endpoint;check(enumerator->GetDevice(id,&endpoint),"exact source endpoint");return endpoint;
}
struct SourceControl {
    Handle ready{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    Handle stop{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    Handle failed{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    DeskFuseTimingCapabilities identity{};
    std::thread worker;
    SourceControl(const wchar_t* id,bool minimum) {
        if (!id || !*id || !ready.get() || !stop.get() || !failed.get()) throw std::runtime_error("source control setup failed");
        worker=std::thread([this,id=std::wstring(id),minimum]{
            try {
                ComApartment com;
                AudioMmcssTask scheduling;
                auto endpoint=exact_endpoint(id.c_str());
                EnginePeriodCompanion period;
                identity=require_corrected_driver(*endpoint.Get());
                if (identity.export_abi!=DESKFUSE_PCM_ABI) throw std::runtime_error("direct ABI unavailable");
                if (minimum) {
                    ComPtr<IAudioClient> audio;
                    check(endpoint->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,reinterpret_cast<void**>(audio.GetAddressOf())),"source format");
                    WAVEFORMATEX* raw=nullptr;check(audio->GetMixFormat(&raw),"source mix format");
                    std::unique_ptr<WAVEFORMATEX,decltype(&CoTaskMemFree)> format(raw,CoTaskMemFree);
                    period.initialize(*endpoint.Get(),*format,engine_periods(*audio.Get(),*format));period.start();
                    period.verify_driver(*endpoint.Get());
                    const auto granted=engine_periods(*audio.Get(),*format);
                    if (FAILED(granted.current_result) || !granted.current_format_matches || granted.current!=period.selected())
                        throw std::runtime_error("direct period not granted");
                }
                SetEvent(ready.get());
                HANDLE waits[]={stop.get(),period.event()};
                for (;;) {
                    const auto result=WaitForMultipleObjects(minimum?2:1,waits,FALSE,INFINITE);
                    if (result==WAIT_OBJECT_0) break;
                    if (minimum && result==WAIT_OBJECT_0+1) period.service();
                    else throw std::runtime_error("period event wait failed");
                }
            } catch (...) {SetEvent(failed.get());SetEvent(ready.get());}
        });
        if (WaitForSingleObject(ready.get(),30000)!=WAIT_OBJECT_0 || WaitForSingleObject(failed.get(),0)==WAIT_OBJECT_0) {
            SetEvent(stop.get());worker.join();throw std::runtime_error("source/period owner failed to initialize");
        }
    }
    ~SourceControl(){SetEvent(stop.get());if(worker.joinable())worker.join();}
};
}
extern "C" __declspec(dllexport) int deskfuse_endpoint_query(const wchar_t* id,DeskFuseTimingCapabilities* result) noexcept {
    if (!result) return -1;
    try {
        ComApartment com;auto endpoint=exact_endpoint(id);auto identity=require_corrected_driver(*endpoint.Get());
        if (identity.export_abi!=DESKFUSE_PCM_ABI) return -1;*result=identity;return 0;
    } catch (...) {return -1;}
}
extern "C" __declspec(dllexport) void* deskfuse_source_open(const wchar_t* id,unsigned minimum,DeskFuseTimingCapabilities* result,HANDLE* event) noexcept {
    if (!result || !event || minimum>1) return nullptr;
    try {auto source=std::make_unique<SourceControl>(id,minimum!=0);*result=source->identity;*event=source->failed.get();return source.release();}
    catch (...) {return nullptr;}
}
extern "C" __declspec(dllexport) int deskfuse_source_service(void* value) noexcept {
    return WaitForSingleObject(static_cast<SourceControl*>(value)->failed.get(),0)==WAIT_OBJECT_0?-1:0;
}
extern "C" __declspec(dllexport) void deskfuse_source_close(void* value) noexcept {delete static_cast<SourceControl*>(value);}
