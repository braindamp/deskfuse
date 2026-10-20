#pragma once

#include "common.h"

#include <winrt/base.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

struct CapturedFrame
{
    int slot = -1;
    uint32_t width = 0;
    uint32_t height = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    int64_t qpcArrival = 0;
    int64_t qpcCopyDone = 0;
    int64_t systemRelativeTime = 0;
    double gpuCopyMs = -1.0;
    bool gpuDisjoint = false;
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame frame{ nullptr };
};

/// Windows.Graphics.Capture of one HWND into persistent D3D11 BGRA textures.
/// The capture path never maps the frame on the CPU; Readback() exists only for
/// the one-off ruler verification frame.
class WgcCapture
{
public:
    WgcCapture(ID3D11Device* device, ID3D11DeviceContext* context, int width, int height, int slotCount = 6);
    ~WgcCapture();

    bool CreateResources(std::wstring& error);
    bool Start(HWND hwnd, std::wstring& error);
    void Stop();

    bool WaitFrame(CapturedFrame& frame, int timeoutMs);
    void ReleaseFrame(const CapturedFrame& frame);
    bool Readback(int slot, std::vector<uint8_t>& bgra, int& width, int& height, std::wstring& error);

    ID3D11Texture2D* SlotTexture(int slot) const;
    int SlotCount() const { return static_cast<int>(slots_.size()); }
    int Width() const { return width_; }
    int Height() const { return height_; }
    DXGI_FORMAT Format() const { return format_; }

    uint64_t Arrived() const { return arrived_; }
    uint64_t WrongSize() const { return wrongSize_; }
    uint64_t DroppedNoSlot() const { return droppedNoSlot_; }
    uint64_t CopyErrors() const { return copyErrors_; }
    uint64_t UnsupportedFormat() const { return unsupportedFormat_; }
    uint64_t SkippedEmpty() const { return skippedEmpty_; }
    std::wstring Error() const;

private:
    enum class SlotState { Free, Copying, Queued };

    struct Slot
    {
        ComPtr<ID3D11Texture2D> texture;
        SlotState state = SlotState::Free;
    };

    void OnFrameArrived();
    void OnClosed(const wchar_t* why);
    bool MaterializeFrame(CapturedFrame& frame, std::wstring& error);
    bool EnsureStaging(std::wstring& error);
    void SetError(const std::wstring& error);

    ID3D11Device* device_ = nullptr;
    ID3D11DeviceContext* context_ = nullptr;
    int width_ = 0;
    int height_ = 0;
    DXGI_FORMAT format_ = DXGI_FORMAT_B8G8R8A8_UNORM;

    std::vector<Slot> slots_;
    mutable std::mutex stateMutex_;
    std::mutex copyMutex_;
    std::condition_variable frameReady_;
    std::deque<CapturedFrame> queue_;
    bool closed_ = false;
    std::wstring error_;

    ComPtr<ID3D11Texture2D> staging_;

    winrt::Windows::Graphics::Capture::GraphicsCaptureItem item_{ nullptr };
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool pool_{ nullptr };
    winrt::Windows::Graphics::Capture::GraphicsCaptureSession session_{ nullptr };
    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice graphicsDevice_{ nullptr };
    winrt::event_token frameArrivedToken_{};
    winrt::event_token itemClosedToken_{};
    bool started_ = false;
    std::atomic<int> callbacks_{ 0 };

    std::atomic<uint64_t> arrived_{ 0 };
    std::atomic<uint64_t> wrongSize_{ 0 };
    std::atomic<uint64_t> droppedNoSlot_{ 0 };
    std::atomic<uint64_t> copyErrors_{ 0 };
    std::atomic<uint64_t> unsupportedFormat_{ 0 };
    std::atomic<uint64_t> skippedEmpty_{ 0 };
};
