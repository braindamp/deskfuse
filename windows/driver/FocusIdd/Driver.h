#pragma once
// =====================================================================================
// FocusIdd — session-scoped indirect display driver (IddCx / UMDF 2), console session.
//
// Modeled on Microsoft's IddSampleDriver (Windows-driver-samples). Product deltas:
//   1. The monitor only ARRIVES on IOCTL_FOCUSIDD_SET_TARGET and departs on
//      IOCTL_FOCUSIDD_REMOVE_MONITOR, D0Exit, or when the agent's heartbeat stops
//      (FR-TGT-02: a session resource, never a permanent display).
//   2. The EDID is synthesized at runtime from the phone profile.
//   3. Frames are published into a shared section for the agent (seqlock, see
//      FocusShared.h) instead of being discarded.
//
// WHAT THIS DRIVER DOES NOT DO — and cannot:
//   It does not set a monitor scale. IddCx's only scale-related field,
//   IDDCX_DISPLAYCONFIGPATH(2).MonitorScaleFactor, is reachable only through
//   IddCxAdapterDisplayConfigUpdate(2), which Microsoft documents as callable by REMOTE
//   session drivers only (IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER; "a console session
//   driver cannot support remote session monitors"). This is a console-session driver,
//   so density is applied by the AGENT after arrival and verified there. See
//   windows/DENSITY.md.
//
// Build: WDK + Visual Studio 2022. NOT compiled in the authoring sandbox (no WDK).
// =====================================================================================
#define NOMINMAX
#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>
#include <dxgi1_5.h>
#include <d3d11_2.h>
#include <avrt.h>
#include <wrl.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <vector>
#include "../../shared/FocusShared.h"
#include "Edid.h"
namespace Microsoft
{
    namespace WRL
    {
        namespace Wrappers
        {
            typedef HandleT<HandleTraits::HANDLENullTraits> Thread;
        }
    }
}
namespace FocusIdd
{
    struct Direct3DDevice
    {
        Direct3DDevice(LUID AdapterLuid);
        Direct3DDevice();
        HRESULT Init();
        LUID AdapterLuid;
        Microsoft::WRL::ComPtr<IDXGIFactory5> DxgiFactory;
        Microsoft::WRL::ComPtr<IDXGIAdapter1> Adapter;
        Microsoft::WRL::ComPtr<ID3D11Device> Device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> DeviceContext;
    };
    /// Owns the shared section the agent reads. Created at adapter init (the UMDF host
    /// process can create objects in the Global\ namespace; the DACL in FocusShared.h
    /// decides who may open them).
    class SharedSurfaceWriter
    {
    public:
        SharedSurfaceWriter();
        ~SharedSurfaceWriter();
        bool Create();
        void Destroy();
        // Returns false (and records FW_ERR_*) if the frame cannot be carried. A frame
        // is NEVER dropped silently: the agent reads LastError/DroppedFrames.
        bool Publish(const D3D11_MAPPED_SUBRESOURCE& Mapped, UINT Width, UINT Height, UINT64 PresentQpc);
        void RejectFrame(UINT32 ErrorCode);
        void SetMonitorArrived(bool Arrived);
        void SetLastError(UINT32 Code);
        UINT64 FramesPublished() const { return m_Seq; }
        UINT64 FramesDropped() const { return m_Header ? m_Header->DroppedFrames : 0; }
        DWORD CreateError() const { return m_CreateError; }
        bool IsReady() const { return m_View != nullptr; }
    private:
        HANDLE m_hSection = nullptr;
        HANDLE m_hFrameEvent = nullptr;
        BYTE* m_View = nullptr;
        FW_SURFACE_HEADER* m_Header = nullptr;
        UINT64 m_Seq = 0;
        UINT32 m_NextSlot = 0;
        DWORD m_CreateError = 0;
    };
    class SwapChainProcessor
    {
    public:
        SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, std::shared_ptr<Direct3DDevice> Device, HANDLE NewFrameEvent, SharedSurfaceWriter* Writer);
        ~SwapChainProcessor();
    private:
        static DWORD CALLBACK RunThread(LPVOID Argument);
        void Run();
        void RunCore();
        bool EnsureStaging(UINT Width, UINT Height);
        IDDCX_SWAPCHAIN m_hSwapChain;
        std::shared_ptr<Direct3DDevice> m_Device;
        HANDLE m_hAvailableBufferEvent;
        SharedSurfaceWriter* m_Writer;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_Staging;
        UINT m_StagingWidth = 0;
        UINT m_StagingHeight = 0;
        Microsoft::WRL::Wrappers::Thread m_hThread;
        Microsoft::WRL::Wrappers::Event m_hTerminateEvent;
    };
    class FocusMonitorContext
    {
    public:
        FocusMonitorContext(_In_ IDDCX_MONITOR Monitor, SharedSurfaceWriter* Writer);
        virtual ~FocusMonitorContext();
        void AssignSwapChain(IDDCX_SWAPCHAIN SwapChain, LUID RenderAdapter, HANDLE NewFrameEvent);
        void UnassignSwapChain();
    private:
        IDDCX_MONITOR m_Monitor;
        SharedSurfaceWriter* m_Writer;
        std::unique_ptr<SwapChainProcessor> m_ProcessingThread;
    };
    class FocusDeviceContext
    {
    public:
        FocusDeviceContext(_In_ WDFDEVICE WdfDevice);
        virtual ~FocusDeviceContext();
        void InitAdapter();
        void OnAdapterInitFinished();
        NTSTATUS SetTarget(const FOCUSIDD_SET_TARGET& Target);
        NTSTATUS RemoveMonitor();
        void GetStatus(FOCUSIDD_STATUS* pStatus);
        // Every IOCTL is a heartbeat for the watchdog.
        void Touch() { m_LastHeartbeatMs = GetTickCount64(); }
        bool GetCurrentEdid(BYTE OutEdid[EdidBuilder::BlockSize]) const;
        bool GetCurrentTarget(FOCUSIDD_SET_TARGET* pTarget) const;
        SharedSurfaceWriter* Writer() { return &m_Writer; }
    private:
        void ApplyPendingTarget();
        void OnWatchdog();
        static void CALLBACK ApplyTargetThunk(PTP_CALLBACK_INSTANCE, PVOID Context, PTP_WORK Work);
        static void CALLBACK WatchdogThunk(PTP_CALLBACK_INSTANCE, PVOID Context, PTP_TIMER Timer);
        WDFDEVICE m_WdfDevice;
        IDDCX_ADAPTER m_Adapter = nullptr;
        IDDCX_MONITOR m_Monitor = nullptr;      // guarded by m_MonitorMutex
        // m_MonitorMutex serializes arrival/departure (long, may block on the OS).
        // m_TargetMutex guards only the small target/EDID copies read by DDI callbacks,
        // which IddCx may invoke synchronously from inside IddCxMonitorArrival — so the
        // two mutexes are never held in the opposite order.
        std::mutex m_MonitorMutex;
        mutable std::mutex m_TargetMutex;
        FOCUSIDD_SET_TARGET m_PendingTarget = {};
        FOCUSIDD_SET_TARGET m_AppliedTarget = {};
        BYTE m_Edid[EdidBuilder::BlockSize] = {};
        bool m_HaveTarget = false;
        std::atomic<bool> m_AdapterReady{ false };
        std::atomic<bool> m_MonitorArrived{ false };
        std::atomic<UINT32> m_LastError{ 0 };
        std::atomic<ULONGLONG> m_LastHeartbeatMs{ 0 };
        SharedSurfaceWriter m_Writer;
        PTP_WORK m_ApplyWork = nullptr;
        PTP_TIMER m_Watchdog = nullptr;
    };
}
