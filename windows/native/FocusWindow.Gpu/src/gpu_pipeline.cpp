#include "focus_window_gpu.h"
#include "capture.h"
#include "nvenc_encoder.h"
#include "common.h"
#include <d3d10.h>
#include <winrt/base.h>
#include <memory>
#include <atomic>
#include <mutex>
#include <algorithm>
#include <cstring>
#include <vector>

struct fwgpu_session {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    std::unique_ptr<WgcCapture> capture;
    std::unique_ptr<NvEncoder> encoder;
    std::vector<uint8_t> packet;
    std::mutex gate;
    std::wstring error;
    uint64_t packetQpc = 0;
    uint64_t lastQpc = 0;
    uint32_t frameNo = 0;
    uint32_t fps = 60;
    bool packetIsIdr = false;
    std::atomic<bool> wantIdr{true};
};
static thread_local std::wstring openError;
static bool HasIdr(const std::vector<uint8_t>& v) {
    for (size_t i=0;i+4<v.size();++i) {
        size_t n=0;
        if(v[i]==0&&v[i+1]==0&&v[i+2]==1) n=3;
        else if(v[i]==0&&v[i+1]==0&&v[i+2]==0&&v[i+3]==1) n=4;
        if(n && i+n<v.size() && (v[i+n]&31)==5) return true;
    }
    return false;
}
FWGPU_EXPORT uint32_t __cdecl fwgpu_abi_version(){return 1;}
FWGPU_EXPORT fwgpu_session* __cdecl fwgpu_open(void* hwnd,uint32_t w,uint32_t h,uint32_t fps,uint32_t bitrate){
    openError.clear();
    if(!hwnd||w<2||h<2||(w&1)||(h&1)||w>8192||h>8192||
        fps<1||fps>240||bitrate<100000||bitrate>150000000) {
        openError=L"Invalid HWND or GPU stream geometry/rate."; return nullptr;
    }
    auto s=std::make_unique<fwgpu_session>(); s->fps=fps;
    try {
        try {winrt::init_apartment(winrt::apartment_type::multi_threaded);}
        catch(const winrt::hresult_error& ex){if(ex.code()!=RPC_E_CHANGED_MODE) throw;}
        D3D_FEATURE_LEVEL level{};
        D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0};
        HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,
          D3D11_CREATE_DEVICE_BGRA_SUPPORT,levels,2,D3D11_SDK_VERSION,
          s->device.GetAddressOf(),&level,s->context.GetAddressOf());
        if(hr==E_INVALIDARG) hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,
          nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,&levels[1],1,
          D3D11_SDK_VERSION,s->device.GetAddressOf(),&level,s->context.GetAddressOf());
        if(FAILED(hr)){s->error=L"D3D11CreateDevice failed";goto failed;}
        ComPtr<ID3D10Multithread> multi;
        if(SUCCEEDED(s->device.As(&multi))&&multi)multi->SetMultithreadProtected(TRUE);
        s->capture=std::make_unique<WgcCapture>(s->device.Get(),s->context.Get(),
            static_cast<int>(w),static_cast<int>(h),6);
        if(!s->capture->CreateResources(s->error))goto failed;
        std::vector<ID3D11Texture2D*> slots;
        for(int i=0;i<s->capture->SlotCount();++i)slots.push_back(s->capture->SlotTexture(i));
        EncoderConfig cfg{};cfg.width=(int)w;cfg.height=(int)h;cfg.fps=(int)fps;cfg.bitrate=(int)bitrate;
        s->encoder=std::make_unique<NvEncoder>();
        if(!s->encoder->Initialize(s->device.Get(),slots.data(),(int)slots.size(),cfg,s->error))
            goto failed;
        if(!s->capture->Start(static_cast<HWND>(hwnd),s->error))goto failed;
        return s.release();
    }catch(const std::exception& e){s->error=Widen(e.what());}
    catch(...){s->error=L"GPU pipeline startup failed";}
failed:
    openError=s->error;return nullptr;
}
FWGPU_EXPORT int32_t __cdecl fwgpu_read(fwgpu_session* s,uint32_t timeoutMs,
    uint8_t* output,uint32_t cap,uint32_t* written,uint64_t* qpc,uint8_t* idr){
    if(!s||!written||!qpc||!idr)return -1;
    std::lock_guard<std::mutex> l(s->gate);
    *written=0;*qpc=0;*idr=0;
    if(s->packet.empty()){
        CapturedFrame frame;
        if(!s->capture->WaitFrame(frame,(int)std::min(timeoutMs,1000u))){
            if(!s->capture->Error().empty()){s->error=s->capture->Error();return -1;}
            return 0;
        }
        const auto stamp=(uint64_t)frame.qpcArrival;
        const auto step=(uint64_t)QpcFrequency()/s->fps;
        if(s->lastQpc&&stamp-s->lastQpc<step){s->capture->ReleaseFrame(frame);return 0;}
        uint32_t bytes=0;double submit=0,lock=0;
        const bool ok=s->encoder->Encode(frame.slot,(uint64_t)frame.systemRelativeTime,
           s->frameNo++,bytes,submit,lock,s->error,s->packet,s->wantIdr.exchange(false));
        s->capture->ReleaseFrame(frame);
        if(!ok)return -1;
        if(s->packet.empty())return 0;
        if(s->packet.size()>8u*1024u*1024u){
            s->packet.clear();s->error=L"Encoded access unit exceeds budget";return -1;
        }
        s->packetQpc=stamp;s->lastQpc=stamp;s->packetIsIdr=HasIdr(s->packet);
    }
    *written=(uint32_t)s->packet.size();
    if(!output||cap<s->packet.size())return -2;
    std::memcpy(output,s->packet.data(),s->packet.size());
    *qpc=s->packetQpc;*idr=s->packetIsIdr?1:0;s->packet.clear();return 1;
}
FWGPU_EXPORT void __cdecl fwgpu_force_idr(fwgpu_session* s){
    if(s)s->wantIdr.store(true);
}
FWGPU_EXPORT int32_t __cdecl fwgpu_last_error(fwgpu_session* s,wchar_t* dest,uint32_t cap){
    const auto& e=s?s->error:openError;
    if(!dest||cap==0)return(int32_t)e.size()+1;
    auto count=std::min<size_t>(e.size(),cap-1);
    wmemcpy(dest,e.data(),count);dest[count]=L'\0';return(int32_t)count;
}
FWGPU_EXPORT void __cdecl fwgpu_close(fwgpu_session* s){
    // Owning caller must join the read worker before closing the native handle.
    delete s;
}
