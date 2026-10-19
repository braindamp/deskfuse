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
#include <thread>

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

    D3D11_QUERY_DESC disjointDesc{};
    disjointDesc.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
    D3D11_QUERY_DESC timestampDesc{};
    timestampDesc.Query = D3D11_QUERY_TIMESTAMP;

    for (auto& slot : slots_)
    {
        HRESULT hr = device_->CreateTexture2D(&desc, nullptr, slot.texture.GetAddressOf());
        if (FAILED(hr))
        {
            error = L"CreateTexture2D for a capture slot failed: " +
                Widen(FormatHresult(hr));
            return false;
        }
        hr = device_->CreateQuery(&disjointDesc, slot.disjoint.GetAddressOf());
        if (FAILED(hr))
        {
            error = L"CreateQuery(disjoint) failed: " + Widen(FormatHresult(hr));
            return false;
        }
        hr = device_->CreateQuery(&timestampDesc, slot.timestampStart.GetAddressOf());
        if (FAILED(hr))
        {
            error = L"CreateQuery(timestamp start) failed: " + Widen(FormatHresult(hr));
            return false;
        }
        hr = device_->CreateQuery(&timestampDesc, slot.timestampEnd.GetAddressOf());
        if (FAILED(hr))
        {
            error = L"CreateQuery(timestamp end) failed: " + Widen(FormatHresult(hr));
            return false;
        }
    }
    return true;
}

bool WgcCapture::Start(HWND hwnd, std::wstring& error)
{
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
        winrt::check_hresult(interop->CreateForWindow(
            hwnd, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item_)));

        const auto itemSize = item_.Size();
        if (itemSize.Width != width_ || itemSize.Height != height_)
        {
            error = L"WGC item size is " + std::to_wstring(itemSize.Width) + L"x" +
                std::to_wstring(itemSize.Height) + L", expected " + std::to_wstring(width_) +
                L"x" + std::to_wstring(height_) + L" (no resize is allowed).";
            return false;
        }

        ComPtr<IDXGIDevice> dxgiDevice;
        winrt::check_hresult(device_->QueryInterface(IID_PPV_ARGS(dxgiDevice.GetAddressOf())));
        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()));
        graphicsDevice_ = inspectable.as<IDirect3DDevice>();

        pool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(
            graphicsDevice_, DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, itemSize);
        session_ = pool_.CreateCaptureSession(item_);
        try { session_.IsCursorCaptureEnabled(false); } catch (...) {}
        try { session_.IsBorderRequired(false); } catch (...) {}

        frameArrivedToken_ = pool_.FrameArrived([this](auto&&, auto&&) { OnFrameArrived(); });
        itemClosedToken_ = item_.Closed([this](auto&&, auto&&) { OnClosed(L"capture item closed"); });

        session_.StartCapture();
        started_ = true;
        return true;
    }
    catch (const winrt::hresult_error& ex)
    {
        error = L"WGC start failed: " + HresultText(ex);
        return false;
    }
    catch (const std::exception& ex)
    {
        error = L"WGC start failed: " + Widen(ex.what());
        return false;
    }
}

void WgcCapture::Stop()
{
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

    // FrameArrived may already be running on the WinRT thread pool. Do not
    // release the frame pool/device while a callback still references them.
    for (int i = 0; i < 300 && callbacks_.load() > 0; ++i)
        Sleep(10);

    session_ = nullptr;
    pool_ = nullptr;
    item_ = nullptr;
    graphicsDevice_ = nullptr;
    started_ = false;
    closed_ = true;
    frameReady_.notify_all();
}

void WgcCapture::OnClosed(const wchar_t* why)
{
    ++callbacks_;
    SetError(why);
    --callbacks_;
}

void WgcCapture::OnFrameArrived()
{
    ++callbacks_;
    struct CallbackScope
    {
        std::atomic<int>& count;
        ~CallbackScope() { --count; }
    } scope{ callbacks_ };
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
            return;
        }

        auto surface = frame.Surface();
        if (!surface)
        {
            ++copyErrors_;
            return;
        }
        auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        winrt::com_ptr<ID3D11Texture2D> source;
        winrt::check_hresult(access->GetInterface(winrt::guid_of<ID3D11Texture2D>(), source.put_void()));
        if (!source)
        {
            ++copyErrors_;
            return;
        }

        D3D11_TEXTURE2D_DESC sourceDesc{};
        source->GetDesc(&sourceDesc);
        if (sourceDesc.Format != format_)
        {
            ++unsupportedFormat_;
            return;
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
            if (slotIndex < 0)
            {
                ++droppedNoSlot_;
                return;
            }
        }

        const int64_t arrival = QpcNow();
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
        UINT64 timestampStart = 0;
        UINT64 timestampEnd = 0;
        HRESULT queryResult = S_OK;
        {
            std::lock_guard<std::mutex> copyLock(copyMutex_);
            auto& slot = slots_[static_cast<size_t>(slotIndex)];
            context_->Begin(slot.disjoint.Get());
            context_->End(slot.timestampStart.Get());
            context_->CopyResource(slot.texture.Get(), source.get());
            context_->End(slot.timestampEnd.Get());
            context_->End(slot.disjoint.Get());
            context_->Flush();

            // Resolve the GPU timestamps before reusing the query. This is a
            // copy+sync boundary only for the diagnostic; the NVENC map would
            // otherwise synchronize the same texture. No CPU pixels are read.
            for (int spin = 0; spin < 100000; ++spin)
            {
                queryResult = context_->GetData(slot.disjoint.Get(), &disjoint, sizeof(disjoint), 0);
                if (queryResult != S_FALSE) break;
                std::this_thread::yield();
            }
            if (queryResult == S_OK)
            {
                context_->GetData(slot.timestampStart.Get(), &timestampStart, sizeof(timestampStart), 0);
                context_->GetData(slot.timestampEnd.Get(), &timestampEnd, sizeof(timestampEnd), 0);
            }
        }

        const int64_t copyDone = QpcNow();
        double gpuCopyMs = -1.0;
        if (queryResult == S_OK && !disjoint.Disjoint && disjoint.Frequency != 0)
            gpuCopyMs = static_cast<double>(timestampEnd - timestampStart) * 1000.0 /
                static_cast<double>(disjoint.Frequency);

        {
            std::lock_guard<std::mutex> lock(stateMutex_);
            auto& slot = slots_[static_cast<size_t>(slotIndex)];
            slot.state = SlotState::Queued;
            CapturedFrame captured{};
            captured.slot = slotIndex;
            captured.width = static_cast<uint32_t>(content.Width);
            captured.height = static_cast<uint32_t>(content.Height);
            captured.format = sourceDesc.Format;
            captured.qpcArrival = arrival;
            captured.qpcCopyDone = copyDone;
            captured.systemRelativeTime = frame.SystemRelativeTime().count();
            captured.gpuCopyMs = gpuCopyMs;
            captured.gpuDisjoint = disjoint.Disjoint != 0;
            queue_.push_back(captured);
            ++arrived_;
            frameReady_.notify_one();
        }
    }
    catch (...)
    {
        ++copyErrors_;
        // A WinRT exception must never escape the event handler.
    }
}

bool WgcCapture::WaitFrame(CapturedFrame& frame, int timeoutMs)
{
    std::unique_lock<std::mutex> lock(stateMutex_);
    if (!frameReady_.wait_for(lock, std::chrono::milliseconds(timeoutMs),
            [this] { return !queue_.empty() || closed_; }))
        return false;
    if (queue_.empty()) return false;
    frame = queue_.front();
    queue_.pop_front();
    return true;
}

void WgcCapture::ReleaseFrame(const CapturedFrame& frame)
{
    {
        std::lock_guard<std::mutex> lock(stateMutex_);
        if (frame.slot >= 0 && frame.slot < static_cast<int>(slots_.size()))
            slots_[static_cast<size_t>(frame.slot)].state = SlotState::Free;
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
        context_->Flush();
        D3D11_MAPPED_SUBRESOURCE mapped{};
        const HRESULT hr = context_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr))
        {
            error = L"Map(staging) failed: " + Widen(FormatHresult(hr));
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
