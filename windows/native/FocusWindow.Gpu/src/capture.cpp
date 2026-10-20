#include "capture.h"

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <algorithm>
#include <iostream>

using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

namespace
{
std::wstring HresultText(const winrt::hresult_error& error)
{
    wchar_t buffer[32]{};
    swprintf_s(buffer, L"0x%08X", static_cast<unsigned>(error.code().value));
    return std::wstring(buffer) + L" (" + std::wstring(error.message().c_str()) + L")";
}
} // namespace

WgcCapture::WgcCapture(ID3D11Device* device, ID3D11DeviceContext* context, int width, int height, int slotCount)
    : device_(device), context_(context), width_(width), height_(height)
{
    slots_.resize(static_cast<size_t>(std::max(2, slotCount)));
    callbacks_->owner = this;
}

WgcCapture::~WgcCapture()
{
    Stop();
}

std::wstring WgcCapture::Error() const
{
    std::lock_guard<std::mutex> lock(stateMutex_);
    return error_;
}

void WgcCapture::SetError(const std::wstring& error)
{
    std::lock_guard<std::mutex> lock(stateMutex_);
    if (error_.empty()) error_ = error;
    closed_ = true;
    frameReady_.notify_all();
}

bool WgcCapture::CreateResources(std::wstring& error)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(width_);
    desc.Height = static_cast<UINT>(height_);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format_;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;

    for (auto& slot : slots_)
    {
        HRESULT hr = device_->CreateTexture2D(&desc, nullptr, slot.texture.GetAddressOf());
        if (FAILED(hr))
        {
            error = L"CreateTexture2D for a capture slot failed: " +
                Widen(FormatHresult(hr));
            return false;
        }
    }
    return true;
}

bool WgcCapture::Start(HWND hwnd, std::wstring& error)
{
    std::wstring stage = L"CreateForWindow";
    try
    {
        if (started_)
        {
            error = L"Capture is already started.";
            return false;
        }
        if (slots_.empty() || !slots_.front().texture)
        {
            error = L"CreateResources must be called before Start.";
            return false;
        }

        auto factory = winrt::get_activation_factory<GraphicsCaptureItem>();
        auto interop = factory.as<IGraphicsCaptureItemInterop>();
        // A just-created window can race DWM registration and return E_INVALIDARG.
        // WS_EX_TOOLWINDOW / taskbar-hidden windows are also rejected on Win10, so
        // the test window deliberately uses a plain top-level style.
        HRESULT createHr = E_FAIL;
        for (int attempt = 0; attempt < 15; ++attempt)
        {
            createHr = interop->CreateForWindow(
                hwnd, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item_));
            if (SUCCEEDED(createHr)) break;
            if (createHr != E_INVALIDARG) break;
            Sleep(100);
        }
        winrt::check_hresult(createHr);

        stage = L"item.Size";
        const auto itemSize = item_.Size();
        if (itemSize.Width != width_ || itemSize.Height != height_)
        {
            error = L"WGC item size is " + std::to_wstring(itemSize.Width) + L"x" +
                std::to_wstring(itemSize.Height) + L", expected " + std::to_wstring(width_) +
                L"x" + std::to_wstring(height_) + L" (no resize is allowed).";
            return false;
        }

        stage = L"CreateDirect3D11DeviceFromDXGIDevice";
        ComPtr<IDXGIDevice> dxgiDevice;
        winrt::check_hresult(device_->QueryInterface(IID_PPV_ARGS(dxgiDevice.GetAddressOf())));
        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()));
        graphicsDevice_ = inspectable.as<IDirect3DDevice>();

        stage = L"CreateFreeThreaded " + std::to_wstring(itemSize.Width) + L"x" +
            std::to_wstring(itemSize.Height);
        pool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(
            graphicsDevice_, DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, itemSize);
        stage = L"CreateCaptureSession";
        session_ = pool_.CreateCaptureSession(item_);
        stage = L"IsCursorCaptureEnabled";
        try { session_.IsCursorCaptureEnabled(false); } catch (...) {}
        stage = L"IsBorderRequired";
        try { session_.IsBorderRequired(false); } catch (...) {}

        stage = L"FrameArrived";
        frameArrivedToken_ = pool_.FrameArrived([state = callbacks_](auto&&, auto&&) { Dispatch(state, false); });
        stage = L"ItemClosed";
        itemClosedToken_ = item_.Closed([state = callbacks_](auto&&, auto&&) { Dispatch(state, true); });

        stage = L"StartCapture";
        session_.StartCapture();
        started_ = true;
        return true;
    }
    catch (const winrt::hresult_error& ex)
    {
        error = L"WGC start failed at " + stage + L": " + HresultText(ex);
        return false;
    }
    catch (const std::exception& ex)
    {
        error = L"WGC start failed at " + stage + L": " + Widen(ex.what());
        return false;
    }
}

void WgcCapture::Stop()
{
    // Queued WinRT callbacks may run after handler revocation. They hold only
    // this shared state, never an unguarded `this` after destruction begins.
    {
        std::lock_guard<std::mutex> lock(callbacks_->gate);
        callbacks_->owner = nullptr;
    }
    if (!started_)
    {
        // Still revoke handlers if Start failed halfway through.
        started_ = false;
    }
    try
    {
        if (pool_) pool_.FrameArrived(frameArrivedToken_);
    }
    catch (...) {}
    try
    {
        if (item_) item_.Closed(itemClosedToken_);
    }
    catch (...) {}
    {
        std::unique_lock<std::mutex> lock(callbacks_->gate);
        callbacks_->drained.wait(lock, [this] { return callbacks_->active == 0; });
    }
    try
    {
        if (session_) session_.Close();
    }
    catch (...) {}
    try
    {
        if (pool_) pool_.Close();
    }
    catch (...) {}

    std::deque<CapturedFrame> pending;
    {
        std::lock_guard<std::mutex> lock(stateMutex_);
        pending.swap(queue_);
        closed_ = true;
    }
    for (auto& frame : pending)
    {
        if (frame.slot >= 0 && frame.slot < static_cast<int>(slots_.size()))
            slots_[static_cast<size_t>(frame.slot)].state = SlotState::Free;
        if (frame.frame)
        {
            try { frame.frame.Close(); } catch (...) {}
            frame.frame = nullptr;
        }
    }

    session_ = nullptr;
    pool_ = nullptr;
    item_ = nullptr;
    graphicsDevice_ = nullptr;
    started_ = false;
    frameReady_.notify_all();
}

void WgcCapture::OnClosed(const wchar_t* why)
{
    SetError(why);
}

void WgcCapture::Dispatch(const std::shared_ptr<CallbackState>& state, bool closed)
{
    WgcCapture* owner;
    {
        std::lock_guard<std::mutex> lock(state->gate);
        owner = state->owner;
        if (!owner) return;
        ++state->active;
    }
    struct Scope
    {
        std::shared_ptr<CallbackState> state;
        ~Scope()
        {
            std::lock_guard<std::mutex> lock(state->gate);
            if (--state->active == 0) state->drained.notify_all();
        }
    } scope{state};
    try
    {
        if (closed) owner->OnClosed(L"capture item closed");
        else owner->OnFrameArrived();
    }
    catch (...) { owner->SetError(L"WGC callback failed"); }
}

void WgcCapture::OnFrameArrived()
{
    try
    {
        auto frame = pool_.TryGetNextFrame();
        if (!frame) return;

        const auto content = frame.ContentSize();
        if (content.Width <= 0 || content.Height <= 0)
        {
            ++skippedEmpty_;
            return;
        }
        if (content.Width != width_ || content.Height != height_)
        {
            ++wrongSize_;
            frame.Close();
            SetError(L"WGC source changed size; reconfigure the session before sending more frames.");
            return;
        }

        // Never do GPU work or wait on a D3D query from the WGC callback. The
        // frame pool owns this thread; blocking it can stall the capture
        // producer and deadlock the copy. Keep the frame alive and materialize
        // it on the caller's thread.
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (closed_) return;
        // Keep ONE latest capture, not a FIFO of stale frames. Dropping before
        // encode does not damage an H.264 GOP. Explicit Close returns the WGC
        // pool surface immediately instead of retaining all three pool frames.
        if (!queue_.empty())
        {
            ++droppedNoSlot_;
            try { if (queue_.front().frame) queue_.front().frame.Close(); } catch (...) {}
            queue_.pop_front();
        }

        CapturedFrame captured{};
        captured.width = static_cast<uint32_t>(content.Width);
        captured.height = static_cast<uint32_t>(content.Height);
        captured.qpcArrival = QpcNow();
        captured.systemRelativeTime = frame.SystemRelativeTime().count();
        captured.frame = frame;
        queue_.push_back(std::move(captured));
        ++arrived_;
        frameReady_.notify_one();
    }
    catch (...)
    {
        ++copyErrors_;
        SetError(L"WGC frame acquisition failed");
    }
}

bool WgcCapture::MaterializeFrame(CapturedFrame& frame, std::wstring& error)
{
    if (!frame.frame)
    {
        error = L"The queued WGC frame is empty.";
        return false;
    }

    auto closeFrame = [&] {
        if (frame.frame)
        {
            try { frame.frame.Close(); } catch (...) {}
            frame.frame = nullptr;
        }
    };

    try
    {
        auto surface = frame.frame.Surface();
        if (!surface)
        {
            ++copyErrors_;
            closeFrame();
            error = L"The WGC frame has no surface.";
            return false;
        }
        auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        winrt::com_ptr<ID3D11Texture2D> source;
        winrt::check_hresult(access->GetInterface(winrt::guid_of<ID3D11Texture2D>(), source.put_void()));
        if (!source)
        {
            ++copyErrors_;
            closeFrame();
            error = L"The WGC surface is not an ID3D11Texture2D.";
            return false;
        }

        D3D11_TEXTURE2D_DESC sourceDesc{};
        source->GetDesc(&sourceDesc);
        if (sourceDesc.Format != format_)
        {
            ++unsupportedFormat_;
            closeFrame();
            error = L"The WGC frame format is " + std::to_wstring(static_cast<unsigned>(sourceDesc.Format)) +
                L", expected " + std::to_wstring(static_cast<unsigned>(format_)) + L".";
            return false;
        }

        ComPtr<ID3D11Device> sourceDevice;
        source->GetDevice(&sourceDevice);
        if (sourceDevice.Get() != device_)
        {
            ++copyErrors_;
            closeFrame();
            error = L"WGC frame texture belongs to a different D3D11 device.";
            return false;
        }
        if (sourceDesc.Width != static_cast<UINT>(width_) || sourceDesc.Height != static_cast<UINT>(height_) ||
            sourceDesc.MipLevels != 1 || sourceDesc.ArraySize != 1 || sourceDesc.SampleDesc.Count != 1)
        {
            closeFrame();
            error = L"WGC surface texture geometry differs from the negotiated source.";
            return false;
        }

        int slotIndex = -1;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            for (size_t i = 0; i < slots_.size(); ++i)
            {
                if (slots_[i].state == SlotState::Free)
                {
                    slots_[i].state = SlotState::Copying;
                    slotIndex = static_cast<int>(i);
                    break;
                }
            }
        }
        if (slotIndex < 0)
        {
            ++droppedNoSlot_;
            closeFrame();
            error = L"No free capture slot for the WGC frame.";
            return false;
        }

        {
            std::lock_guard<std::mutex> copyLock(copyMutex_);
            auto& slot = slots_[static_cast<size_t>(slotIndex)];
            context_->CopyResource(slot.texture.Get(), source.get());
            context_->Flush();
        }

        frame.slot = slotIndex;
        frame.format = sourceDesc.Format;
        frame.qpcCopyDone = QpcNow();
        frame.gpuCopyMs = -1.0;
        frame.gpuDisjoint = false;
        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            slots_[static_cast<size_t>(slotIndex)].state = SlotState::Queued;
        }
        closeFrame();
        return true;
    }
    catch (const winrt::hresult_error& ex)
    {
        ++copyErrors_;
        error = L"WGC frame materialization failed: " + HresultText(ex);
        closeFrame();
        return false;
    }
    catch (const std::exception& ex)
    {
        ++copyErrors_;
        error = L"WGC frame materialization failed: " + Widen(ex.what());
        closeFrame();
        return false;
    }
}

bool WgcCapture::WaitFrame(CapturedFrame& frame, int timeoutMs, int64_t minimumQpc, int64_t afterSourceTime)
{
    CapturedFrame pending;
    {
        std::unique_lock<std::mutex> lock(stateMutex_);
        if (!frameReady_.wait_for(lock, std::chrono::milliseconds(timeoutMs),
                [this] { return !queue_.empty() || closed_; }))
            return false;
        if (queue_.empty()) return false;
        pending = std::move(queue_.front());
        queue_.pop_front();
    }

    // Pace and reject repeated compositor timestamps BEFORE any GPU copy.
    // A different picture is never guessed from frame arrival cadence alone.
    if (pending.qpcArrival < minimumQpc ||
        (afterSourceTime != 0 && pending.systemRelativeTime <= afterSourceTime))
    {
        try { if (pending.frame) pending.frame.Close(); } catch (...) {}
        return false;
    }

    std::wstring materializeError;
    if (!MaterializeFrame(pending, materializeError))
    {
        SetError(materializeError);
        return false;
    }
    frame = std::move(pending);
    return true;
}

void WgcCapture::ReleaseFrame(const CapturedFrame& frame)
{
    {
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (frame.slot >= 0 && frame.slot < static_cast<int>(slots_.size()))
            slots_[static_cast<size_t>(frame.slot)].state = SlotState::Free;
    }
    if (frame.frame)
    {
        try { frame.frame.Close(); } catch (...) {}
    }
    frameReady_.notify_all();
}

ID3D11Texture2D* WgcCapture::SlotTexture(int slot) const
{
    if (slot < 0 || slot >= static_cast<int>(slots_.size())) return nullptr;
    return slots_[static_cast<size_t>(slot)].texture.Get();
}

bool WgcCapture::EnsureStaging(std::wstring& error)
{
    if (staging_) return true;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(width_);
    desc.Height = static_cast<UINT>(height_);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format_;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    const HRESULT hr = device_->CreateTexture2D(&desc, nullptr, staging_.GetAddressOf());
    if (FAILED(hr))
    {
        error = L"CreateTexture2D(staging) failed: " + Widen(FormatHresult(hr));
        return false;
    }
    return true;
}

bool WgcCapture::Readback(int slot, std::vector<uint8_t>& bgra, int& width, int& height, std::wstring& error)
{
    if (slot < 0 || slot >= static_cast<int>(slots_.size()))
    {
        error = L"Readback slot is out of range.";
        return false;
    }
    if (!EnsureStaging(error)) return false;

    {
        std::lock_guard<std::mutex> copyLock(copyMutex_);
        context_->CopyResource(staging_.Get(), slots_[static_cast<size_t>(slot)].texture.Get());
        // Map on the caller thread blocks until the copy completes. This is the
        // one-off ruler path only; the steady path never maps a texture.
        D3D11_MAPPED_SUBRESOURCE mapped{};
        const HRESULT hr = context_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr))
        {
            const HRESULT removed = device_->GetDeviceRemovedReason();
            error = L"Map(staging) failed: " + Widen(FormatHresult(hr)) +
                L"; device removed reason=" + Widen(FormatHresult(removed));
            return false;
        }
        width = width_;
        height = height_;
        bgra.resize(static_cast<size_t>(width_) * static_cast<size_t>(height_) * 4);
        for (int y = 0; y < height_; ++y)
        {
            const auto* source = static_cast<const uint8_t*>(mapped.pData) +
                static_cast<size_t>(y) * mapped.RowPitch;
            std::copy(source, source + static_cast<size_t>(width_) * 4,
                bgra.data() + static_cast<size_t>(y) * static_cast<size_t>(width_) * 4);
        }
        context_->Unmap(staging_.Get(), 0);
    }
    return true;
}
