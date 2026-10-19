// =====================================================================================
// FocusIdd — session-scoped indirect display driver.
//
// UNVERIFIED BUILD: this file needs the Windows Driver Kit (iddcx.h, UMDF tooling) and
// was NOT compiled or run in the authoring environment. The pure logic it depends on
// (Edid.cpp, the shared-memory contract) IS compiled and tested — see tests/.
// Structure follows Microsoft's IddSampleDriver wherever the OS contract is involved;
// product-specific code is marked [FOCUS].
//
// Lifecycle:
//   DriverEntry -> DeviceAdd -> D0Entry -> InitAdapter -> AdapterInitFinished
//   (no monitor) -> SET_TARGET -> EDID -> MonitorCreate/Arrival -> AssignSwapChain
//   -> frames into the shared section -> REMOVE_MONITOR | D0Exit | heartbeat timeout
//   -> departure.
// =====================================================================================
#include "Driver.h"
#include <sddl.h>
#include <strsafe.h>
using namespace std;
using namespace Microsoft::WRL;
using namespace FocusIdd;
#pragma region Forward declarations
extern "C" DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD IddFocusDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY IddFocusDeviceD0Entry;
EVT_WDF_DEVICE_D0_EXIT IddFocusDeviceD0Exit;
EVT_IDD_CX_ADAPTER_INIT_FINISHED IddFocusAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES IddFocusAdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION IddFocusParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES IddFocusMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES IddFocusMonitorQueryModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN IddFocusMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN IddFocusMonitorUnassignSwapChain;
EVT_IDD_CX_DEVICE_IO_CONTROL IddFocusDeviceIoControl;
struct FocusDeviceContextWrapper
{
    FocusDeviceContext* pContext;
    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};
struct FocusMonitorContextWrapper
{
    FocusMonitorContext* pContext;
    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};
WDF_DECLARE_CONTEXT_TYPE(FocusDeviceContextWrapper);
WDF_DECLARE_CONTEXT_TYPE(FocusMonitorContextWrapper);
// [FOCUS] EvtIddCxParseMonitorDescription receives only the EDID bytes (no monitor or
// adapter handle), so recognising "our" EDID needs the device context. This is a single
// root-enumerated device, so one process-wide pointer is sufficient.
static FocusDeviceContext* g_DeviceContext = nullptr;
#pragma endregion
#pragma region Helpers
static inline void FillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& Mode, DWORD Width, DWORD Height, DWORD VSync, bool bMonitorMode)
{
    Mode.totalSize.cx = Mode.activeSize.cx = Width;
    Mode.totalSize.cy = Mode.activeSize.cy = Height;
    Mode.AdditionalSignalInfo.vSyncFreqDivider = bMonitorMode ? 0 : 1;
    Mode.AdditionalSignalInfo.videoStandard = 255;
    Mode.vSyncFreq.Numerator = VSync;
    Mode.vSyncFreq.Denominator = 1;
    Mode.hSyncFreq.Numerator = VSync * Height;
    Mode.hSyncFreq.Denominator = 1;
    Mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    Mode.pixelRate = ((UINT64)VSync) * ((UINT64)Width) * ((UINT64)Height);
}
static IDDCX_MONITOR_MODE CreateMonitorMode(DWORD Width, DWORD Height, DWORD VSync, IDDCX_MONITOR_MODE_ORIGIN Origin)
{
    IDDCX_MONITOR_MODE Mode = {};
    Mode.Size = sizeof(Mode);
    Mode.Origin = Origin;
    FillSignalInfo(Mode.MonitorVideoSignalInfo, Width, Height, VSync, true);
    return Mode;
}
static IDDCX_TARGET_MODE CreateTargetMode(DWORD Width, DWORD Height, DWORD VSync)
{
    IDDCX_TARGET_MODE Mode = {};
    Mode.Size = sizeof(Mode);
    FillSignalInfo(Mode.TargetVideoSignalInfo.targetVideoSignalInfo, Width, Height, VSync, false);
    return Mode;
}
namespace
{
    // The security descriptor returned by ConvertStringSecurityDescriptor... must be
    // LocalFree'd, and must stay alive for as long as any SECURITY_ATTRIBUTES points at
    // it. v1 freed it right after the first CreateFileMapping and then reused the dangling
    // pointer for CreateEvent (use-after-free). RAII + creating both objects inside one
    // scope removes the whole class of mistake.
    struct LocalFreeDeleter
    {
        void operator()(void* p) const
        {
            if (p) LocalFree(p);
        }
    };
    using SdPtr = unique_ptr<void, LocalFreeDeleter>;
    SdPtr MakeSecurityDescriptor(PCWSTR Sddl, DWORD* pError)
    {
        PSECURITY_DESCRIPTOR Sd = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, SDDL_REVISION_1, &Sd, nullptr))
        {
            *pError = GetLastError();
            return nullptr;
        }
        return SdPtr(Sd);
    }
}
#pragma endregion
#pragma region Entry points
extern "C" BOOL WINAPI DllMain(_In_ HINSTANCE hInstance, _In_ UINT dwReason, _In_opt_ LPVOID lpReserved)
{
    UNREFERENCED_PARAMETER(hInstance);
    UNREFERENCED_PARAMETER(lpReserved);
    UNREFERENCED_PARAMETER(dwReason);
    return TRUE;
}
_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT pDriverObject, PUNICODE_STRING pRegistryPath)
{
    WDF_DRIVER_CONFIG Config;
    WDF_OBJECT_ATTRIBUTES Attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&Attributes);
    WDF_DRIVER_CONFIG_INIT(&Config, IddFocusDeviceAdd);
    return WdfDriverCreate(pDriverObject, pRegistryPath, &Attributes, &Config, WDF_NO_HANDLE);
}
_Use_decl_annotations_
NTSTATUS IddFocusDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT pDeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);
    NTSTATUS Status = STATUS_SUCCESS;
    WDF_PNPPOWER_EVENT_CALLBACKS PnpPowerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&PnpPowerCallbacks);
    PnpPowerCallbacks.EvtDeviceD0Entry = IddFocusDeviceD0Entry;
    PnpPowerCallbacks.EvtDeviceD0Exit = IddFocusDeviceD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(pDeviceInit, &PnpPowerCallbacks);
    IDD_CX_CLIENT_CONFIG IddConfig;
    IDD_CX_CLIENT_CONFIG_INIT(&IddConfig);
    // [FOCUS] IddCx redirects IoDeviceControl to an internal queue, so custom IOCTLs must
    // arrive through this callback.
    IddConfig.EvtIddCxDeviceIoControl = IddFocusDeviceIoControl;
    IddConfig.EvtIddCxAdapterInitFinished = IddFocusAdapterInitFinished;
    IddConfig.EvtIddCxParseMonitorDescription = IddFocusParseMonitorDescription;
    IddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = IddFocusMonitorGetDefaultModes;
    IddConfig.EvtIddCxMonitorQueryTargetModes = IddFocusMonitorQueryModes;
    IddConfig.EvtIddCxAdapterCommitModes = IddFocusAdapterCommitModes;
    IddConfig.EvtIddCxMonitorAssignSwapChain = IddFocusMonitorAssignSwapChain;
    IddConfig.EvtIddCxMonitorUnassignSwapChain = IddFocusMonitorUnassignSwapChain;
    Status = IddCxDeviceInitConfig(pDeviceInit, &IddConfig);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }
    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, FocusDeviceContextWrapper);
    Attr.EvtCleanupCallback = [](WDFOBJECT Object)
    {
        auto* pContext = WdfObjectGet_FocusDeviceContextWrapper(Object);
        if (pContext)
        {
            pContext->Cleanup();
        }
    };
    WDFDEVICE Device = nullptr;
    Status = WdfDeviceCreate(&pDeviceInit, &Attr, &Device);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }
    Status = IddCxDeviceInitialize(Device);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }
    // [FOCUS] Control interface: the agent finds it with SetupDiEnumDeviceInterfaces.
    Status = WdfDeviceCreateDeviceInterface(Device, &GUID_DEVINTERFACE_FOCUSIDD, nullptr);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }
    auto* pContext = WdfObjectGet_FocusDeviceContextWrapper(Device);
    pContext->pContext = new FocusDeviceContext(Device);
    g_DeviceContext = pContext->pContext;
    return Status;
}
_Use_decl_annotations_
NTSTATUS IddFocusDeviceD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);
    auto* pContext = WdfObjectGet_FocusDeviceContextWrapper(Device);
    pContext->pContext->InitAdapter();
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS IddFocusDeviceD0Exit(WDFDEVICE Device, WDF_POWER_DEVICE_STATE TargetState)
{
    UNREFERENCED_PARAMETER(TargetState);
    // [FOCUS] The display topology must return to baseline even if the agent died
    // without asking (FR-TGT-02).
    auto* pContext = WdfObjectGet_FocusDeviceContextWrapper(Device);
    if (pContext && pContext->pContext)
    {
        pContext->pContext->RemoveMonitor();
    }
    return STATUS_SUCCESS;
}
#pragma endregion
#pragma region Direct3DDevice
Direct3DDevice::Direct3DDevice(LUID AdapterLuid) : AdapterLuid(AdapterLuid)
{
}
Direct3DDevice::Direct3DDevice()
{
    AdapterLuid = LUID{};
}
HRESULT Direct3DDevice::Init()
{
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&DxgiFactory));
    if (FAILED(hr))
    {
        return hr;
    }
    ComPtr<IDXGIFactory4> Factory4;
    hr = DxgiFactory.As(&Factory4);
    if (SUCCEEDED(hr))
    {
        hr = Factory4->EnumAdapterByLuid(AdapterLuid, IID_PPV_ARGS(&Adapter));
    }
    if (FAILED(hr) || !Adapter)
    {
        hr = DxgiFactory->EnumAdapters1(0, &Adapter);
        if (FAILED(hr))
        {
            return hr;
        }
    }
    UINT Flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL FeatureLevels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    return D3D11CreateDevice(
        Adapter.Get(),
        D3D_DRIVER_TYPE_UNKNOWN,
        nullptr,
        Flags,
        FeatureLevels,
        ARRAYSIZE(FeatureLevels),
        D3D11_SDK_VERSION,
        &Device,
        nullptr,
        &DeviceContext);
}
#pragma endregion
#pragma region SharedSurfaceWriter
SharedSurfaceWriter::SharedSurfaceWriter() = default;
SharedSurfaceWriter::~SharedSurfaceWriter()
{
    Destroy();
}
bool SharedSurfaceWriter::Create()
{
    if (m_View)
    {
        return true;
    }
    // Both descriptors live until this function returns, i.e. past BOTH object
    // creations. (v1 freed the descriptor between them: use-after-free.)
    DWORD Error = 0;
    SdPtr SectionSd = MakeSecurityDescriptor(FW_SDDL_SECTION, &Error);
    SdPtr EventSd = SectionSd ? MakeSecurityDescriptor(FW_SDDL_EVENT, &Error) : nullptr;
    if (!SectionSd || !EventSd)
    {
        m_CreateError = Error;
        return false;
    }
    SECURITY_ATTRIBUTES SectionAttr = { sizeof(SECURITY_ATTRIBUTES), SectionSd.get(), FALSE };
    SECURITY_ATTRIBUTES EventAttr = { sizeof(SECURITY_ATTRIBUTES), EventSd.get(), FALSE };
    m_hSection = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        &SectionAttr,
        PAGE_READWRITE | SEC_COMMIT,
        0,
        FW_SECTION_BYTES,
        FW_SECTION_NAME);
    if (!m_hSection)
    {
        m_CreateError = GetLastError();
        return false;
    }
    m_hFrameEvent = CreateEventW(&EventAttr, FALSE, FALSE, FW_FRAME_EVENT_NAME);
    if (!m_hFrameEvent)
    {
        m_CreateError = GetLastError();
        Destroy();
        return false;
    }
    m_View = (BYTE*)MapViewOfFile(m_hSection, FILE_MAP_ALL_ACCESS, 0, 0, FW_SECTION_BYTES);
    if (!m_View)
    {
        m_CreateError = GetLastError();
        Destroy();
        return false;
    }
    memset(m_View, 0, FW_HEADER_BYTES);
    m_Header = reinterpret_cast<FW_SURFACE_HEADER*>(m_View);
    m_Header->Version = FW_SURFACE_VERSION;
    m_Header->SlotCount = FW_SLOT_COUNT;
    m_Header->SlotStrideBytes = FW_SLOT_STRIDE_BYTES;
    m_Header->PixelFormat = FW_PIXEL_FORMAT_BGRA8;
    m_Header->MaxPixels = FW_MAX_PIXELS;
    MemoryBarrier();
    m_Header->Magic = FW_SURFACE_MAGIC; // published last: the reader keys off this
    m_CreateError = 0;
    return true;
}
void SharedSurfaceWriter::Destroy()
{
    if (m_View)
    {
        UnmapViewOfFile(m_View);
        m_View = nullptr;
        m_Header = nullptr;
    }
    if (m_hFrameEvent)
    {
        CloseHandle(m_hFrameEvent);
        m_hFrameEvent = nullptr;
    }
    if (m_hSection)
    {
        CloseHandle(m_hSection);
        m_hSection = nullptr;
    }
}
void SharedSurfaceWriter::RejectFrame(UINT32 ErrorCode)
{
    if (!m_Header) return;
    m_Header->LastError = ErrorCode;
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&m_Header->DroppedFrames));
}
bool SharedSurfaceWriter::Publish(const D3D11_MAPPED_SUBRESOURCE& Mapped, UINT Width, UINT Height, UINT64 PresentQpc)
{
    if (!m_Header)
    {
        return false;
    }
    // v1 limited each AXIS to 2560, which made a 1440x3120 phone impossible. The limit is
    // now on total pixels (a flat byte budget), with each axis bounded by what an EDID
    // timing can describe.
    if (Width == 0 || Height == 0 ||
        Width > FW_MAX_DIMENSION || Height > FW_MAX_DIMENSION ||
        (UINT64)Width * (UINT64)Height > (UINT64)FW_MAX_PIXELS)
    {
        RejectFrame(FW_ERR_FRAME_TOO_LARGE);
        return false;
    }
    const UINT32 SlotIndex = m_NextSlot;
    m_NextSlot = (m_NextSlot + 1) % FW_SLOT_COUNT;
    BYTE* SlotBase = m_View + FW_HEADER_BYTES + (size_t)SlotIndex * FW_SLOT_STRIDE_BYTES;
    auto* SlotHeader = reinterpret_cast<FW_SLOT_HEADER*>(SlotBase);
    BYTE* Pixels = SlotBase + FW_SLOT_HEADER_BYTES;
    // SEQLOCK: take the slot away from readers BEFORE touching its pixels. Without this,
    // a reader that started copying this slot while it still carried its old Seq would
    // see the same Seq before and after the copy and accept torn pixels.
    SlotHeader->Seq = 0;
    MemoryBarrier();
    const UINT DstPitch = Width * 4;
    const UINT CopyPitch = min<UINT>(Mapped.RowPitch, DstPitch);
    const BYTE* Src = (const BYTE*)Mapped.pData;
    for (UINT Row = 0; Row < Height; Row++)
    {
        memcpy(Pixels + (size_t)Row * DstPitch, Src + (size_t)Row * Mapped.RowPitch, CopyPitch);
    }
    SlotHeader->Width = Width;
    SlotHeader->Height = Height;
    SlotHeader->PresentQpc = PresentQpc;
    MemoryBarrier();
    const UINT64 Seq = ++m_Seq;
    SlotHeader->Seq = Seq;
    MemoryBarrier();
    m_Header->Width = Width;
    m_Header->Height = Height;
    m_Header->Stride = DstPitch;
    m_Header->LatestSlotIndex = SlotIndex;
    MemoryBarrier();
    m_Header->PublishedSeq = Seq;
    if (m_hFrameEvent)
    {
        SetEvent(m_hFrameEvent);
    }
    return true;
}
void SharedSurfaceWriter::SetMonitorArrived(bool Arrived)
{
    if (m_Header)
    {
        m_Header->MonitorArrived = Arrived ? 1 : 0;
    }
}
void SharedSurfaceWriter::SetLastError(UINT32 Code)
{
    if (m_Header)
    {
        m_Header->LastError = Code;
    }
}
#pragma endregion
#pragma region SwapChainProcessor
SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, shared_ptr<Direct3DDevice> Device, HANDLE NewFrameEvent, SharedSurfaceWriter* Writer)
    : m_hSwapChain(hSwapChain), m_Device(Device), m_hAvailableBufferEvent(NewFrameEvent), m_Writer(Writer)
{
    m_hTerminateEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));
    m_hThread.Attach(CreateThread(nullptr, 0, RunThread, this, 0, nullptr));
}
SwapChainProcessor::~SwapChainProcessor()
{
    SetEvent(m_hTerminateEvent.Get());
    if (m_hThread.Get())
    {
        WaitForSingleObject(m_hThread.Get(), INFINITE);
    }
}
DWORD CALLBACK SwapChainProcessor::RunThread(LPVOID Argument)
{
    reinterpret_cast<SwapChainProcessor*>(Argument)->Run();
    return 0;
}
bool SwapChainProcessor::EnsureStaging(UINT Width, UINT Height)
{
    if (m_Staging && m_StagingWidth == Width && m_StagingHeight == Height)
    {
        return true;
    }
    D3D11_TEXTURE2D_DESC Desc = {};
    Desc.Width = Width;
    Desc.Height = Height;
    Desc.MipLevels = 1;
    Desc.ArraySize = 1;
    Desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    Desc.SampleDesc.Count = 1;
    Desc.Usage = D3D11_USAGE_STAGING;
    Desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    m_Staging.Reset();
    if (FAILED(m_Device->Device->CreateTexture2D(&Desc, nullptr, &m_Staging)))
    {
        return false;
    }
    m_StagingWidth = Width;
    m_StagingHeight = Height;
    return true;
}
void SwapChainProcessor::Run()
{
    DWORD AvTask = 0;
    HANDLE AvTaskHandle = AvSetMmThreadCharacteristicsW(L"Distribution", &AvTask);
    RunCore();
    // Always delete the swap chain when processing ends so the OS provides a new one.
    WdfObjectDelete((WDFOBJECT)m_hSwapChain);
    m_hSwapChain = nullptr;
    if (AvTaskHandle)
    {
        AvRevertMmThreadCharacteristics(AvTaskHandle);
    }
}
void SwapChainProcessor::RunCore()
{
    ComPtr<IDXGIDevice> DxgiDevice;
    if (FAILED(m_Device->Device.As(&DxgiDevice)))
    {
        return;
    }
    IDARG_IN_SWAPCHAINSETDEVICE SetDevice = {};
    SetDevice.pDevice = DxgiDevice.Get();
    if (FAILED(IddCxSwapChainSetDevice(m_hSwapChain, &SetDevice)))
    {
        return;
    }
    for (;;)
    {
        ComPtr<IDXGIResource> AcquiredBuffer;
        IDARG_OUT_RELEASEANDACQUIREBUFFER Buffer = {};
        HRESULT hr = IddCxSwapChainReleaseAndAcquireBuffer(m_hSwapChain, &Buffer);
        if (hr == E_PENDING)
        {
            HANDLE WaitHandles[] = { m_hAvailableBufferEvent, m_hTerminateEvent.Get() };
            DWORD WaitResult = WaitForMultipleObjects(ARRAYSIZE(WaitHandles), WaitHandles, FALSE, 16);
            if (WaitResult == WAIT_OBJECT_0 || WaitResult == WAIT_TIMEOUT)
            {
                continue;
            }
            break; // terminate event, or the wait failed
        }
        else if (SUCCEEDED(hr))
        {
            AcquiredBuffer.Attach(Buffer.MetaData.pSurface);
            ComPtr<ID3D11Texture2D> SurfaceTex;
            if (SUCCEEDED(AcquiredBuffer.As(&SurfaceTex)))
            {
                D3D11_TEXTURE2D_DESC Desc;
                SurfaceTex->GetDesc(&Desc);
                LARGE_INTEGER Qpc;
                QueryPerformanceCounter(&Qpc);
                // v1 silently ignored every non-BGRA8 surface (HDR / advanced color
                // delivers other formats) and the agent saw "no frames" with no clue why.
                // Now the rejection is counted and the error code is published.
                if (Desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && Desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
                {
                    m_Writer->RejectFrame(FW_ERR_UNSUPPORTED_FORMAT);
                }
                else if (EnsureStaging(Desc.Width, Desc.Height))
                {
                    // [FOCUS] GPU copy into a staging texture, then ONE mapped copy into
                    // the shared section. Simple and correct; a zero-copy shared-texture
                    // handoff is the optimization once this is measured on real hardware.
                    m_Device->DeviceContext->CopyResource(m_Staging.Get(), SurfaceTex.Get());
                    D3D11_MAPPED_SUBRESOURCE Mapped = {};
                    if (SUCCEEDED(m_Device->DeviceContext->Map(m_Staging.Get(), 0, D3D11_MAP_READ, 0, &Mapped)))
                    {
                        m_Writer->Publish(Mapped, Desc.Width, Desc.Height, (UINT64)Qpc.QuadPart);
                        m_Device->DeviceContext->Unmap(m_Staging.Get(), 0);
                    }
                }
            }
            AcquiredBuffer.Reset();
            if (FAILED(IddCxSwapChainFinishedProcessingFrame(m_hSwapChain)))
            {
                break;
            }
        }
        else
        {
            break; // swap chain abandoned (e.g. DXGI_ERROR_ACCESS_LOST)
        }
    }
}
#pragma endregion
#pragma region FocusMonitorContext
FocusMonitorContext::FocusMonitorContext(_In_ IDDCX_MONITOR Monitor, SharedSurfaceWriter* Writer)
    : m_Monitor(Monitor), m_Writer(Writer)
{
}
FocusMonitorContext::~FocusMonitorContext()
{
    m_ProcessingThread.reset();
}
void FocusMonitorContext::AssignSwapChain(IDDCX_SWAPCHAIN SwapChain, LUID RenderAdapter, HANDLE NewFrameEvent)
{
    m_ProcessingThread.reset();
    auto Device = make_shared<Direct3DDevice>(RenderAdapter);
    if (FAILED(Device->Init()))
    {
        WdfObjectDelete(SwapChain);
    }
    else
    {
        m_ProcessingThread.reset(new SwapChainProcessor(SwapChain, Device, NewFrameEvent, m_Writer));
    }
}
void FocusMonitorContext::UnassignSwapChain()
{
    m_ProcessingThread.reset();
}
#pragma endregion
#pragma region FocusDeviceContext
FocusDeviceContext::FocusDeviceContext(_In_ WDFDEVICE WdfDevice) : m_WdfDevice(WdfDevice)
{
    m_LastHeartbeatMs = GetTickCount64();
}
FocusDeviceContext::~FocusDeviceContext()
{
    if (m_Watchdog)
    {
        SetThreadpoolTimer(m_Watchdog, nullptr, 0, 0);
        WaitForThreadpoolTimerCallbacks(m_Watchdog, TRUE);
        CloseThreadpoolTimer(m_Watchdog);
        m_Watchdog = nullptr;
    }
    if (m_ApplyWork)
    {
        WaitForThreadpoolWorkCallbacks(m_ApplyWork, TRUE);
        CloseThreadpoolWork(m_ApplyWork);
        m_ApplyWork = nullptr;
    }
    m_Writer.Destroy();
    if (g_DeviceContext == this)
    {
        g_DeviceContext = nullptr;
    }
}
void FocusDeviceContext::InitAdapter()
{
    m_Writer.Create();
    // [FOCUS] Watchdog: if the agent crashes while a monitor exists, nothing would ever
    // ask us to remove it. Every IOCTL is a heartbeat; silence for
    // FOCUSIDD_HEARTBEAT_TIMEOUT_MS means the agent is gone.
    if (!m_Watchdog)
    {
        m_Watchdog = CreateThreadpoolTimer(&FocusDeviceContext::WatchdogThunk, this, nullptr);
        if (m_Watchdog)
        {
            ULARGE_INTEGER Due;
            Due.QuadPart = (ULONGLONG)(-(LONGLONG)FOCUSIDD_WATCHDOG_PERIOD_MS * 10000);
            FILETIME Ft;
            Ft.dwLowDateTime = Due.LowPart;
            Ft.dwHighDateTime = Due.HighPart;
            SetThreadpoolTimer(m_Watchdog, &Ft, FOCUSIDD_WATCHDOG_PERIOD_MS, 100);
        }
    }
    IDDCX_ADAPTER_CAPS AdapterCaps = {};
    AdapterCaps.Size = sizeof(AdapterCaps);
    // Console-session driver: Flags stays 0. Setting IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER
    // would make IddCxAdapterInitAsync FAIL, because the OS only allows it for devices created
    // by the remote-desktop stack. It is also what would gate IddCxAdapterDisplayConfigUpdate
    // (the only API that carries MonitorScaleFactor) — which is why we cannot use it.
    AdapterCaps.MaxMonitorsSupported = 1;
    AdapterCaps.EndPointDiagnostics.Size = sizeof(AdapterCaps.EndPointDiagnostics);
    AdapterCaps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    AdapterCaps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    AdapterCaps.EndPointDiagnostics.pEndPointFriendlyName = L"Focus Window virtual display";
    AdapterCaps.EndPointDiagnostics.pEndPointManufacturerName = L"Focus Window";
    AdapterCaps.EndPointDiagnostics.pEndPointModelName = L"FocusPhone virtual monitor";
    IDDCX_ENDPOINT_VERSION Version = {};
    Version.Size = sizeof(Version);
    Version.MajorVer = 1;
    AdapterCaps.EndPointDiagnostics.pFirmwareVersion = &Version;
    AdapterCaps.EndPointDiagnostics.pHardwareVersion = &Version;
    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, FocusDeviceContextWrapper);
    IDARG_IN_ADAPTER_INIT AdapterInit = {};
    AdapterInit.WdfDevice = m_WdfDevice;
    AdapterInit.pCaps = &AdapterCaps;
    AdapterInit.ObjectAttributes = &Attr;
    IDARG_OUT_ADAPTER_INIT AdapterInitOut;
    NTSTATUS Status = IddCxAdapterInitAsync(&AdapterInit, &AdapterInitOut);
    if (NT_SUCCESS(Status))
    {
        m_Adapter = AdapterInitOut.AdapterObject;
        auto* pContext = WdfObjectGet_FocusDeviceContextWrapper(AdapterInitOut.AdapterObject);
        pContext->pContext = this;
    }
    else
    {
        m_LastError = (UINT32)Status;
        m_Writer.SetLastError((UINT32)Status);
    }
}
void FocusDeviceContext::OnAdapterInitFinished()
{
    // Unlike the sample we report NO monitor here: the monitor exists only for the length
    // of a session, so the user's desktop never carries a stray display between sessions.
    m_AdapterReady = true;
    bool HavePending;
    {
        lock_guard<mutex> Lock(m_TargetMutex);
        HavePending = m_HaveTarget;
    }
    if (HavePending)
    {
        ApplyPendingTarget();
    }
}
bool FocusDeviceContext::GetCurrentEdid(BYTE OutEdid[EdidBuilder::BlockSize]) const
{
    lock_guard<mutex> Lock(m_TargetMutex);
    if (!m_HaveTarget)
    {
        return false;
    }
    memcpy(OutEdid, m_Edid, EdidBuilder::BlockSize);
    return true;
}
bool FocusDeviceContext::GetCurrentTarget(FOCUSIDD_SET_TARGET* pTarget) const
{
    lock_guard<mutex> Lock(m_TargetMutex);
    if (!m_HaveTarget)
    {
        return false;
    }
    *pTarget = m_AppliedTarget;
    return true;
}
NTSTATUS FocusDeviceContext::SetTarget(const FOCUSIDD_SET_TARGET& Target)
{
    // Rejection is explicit and carries a reason through LastError; it is never a silent clamp.
    if (Target.WidthPx < 320 || Target.HeightPx < 320 ||
        Target.WidthPx > FW_MAX_DIMENSION || Target.HeightPx > FW_MAX_DIMENSION ||
        (UINT64)Target.WidthPx * (UINT64)Target.HeightPx > (UINT64)FW_MAX_PIXELS ||
        Target.RefreshHz < 24 || Target.RefreshHz > 240 ||
        Target.PhysWidthMm == 0 || Target.PhysHeightMm == 0)
    {
        m_LastError = (UINT32)STATUS_INVALID_PARAMETER;
        m_Writer.SetLastError((UINT32)STATUS_INVALID_PARAMETER);
        return STATUS_INVALID_PARAMETER;
    }
    {
        lock_guard<mutex> Lock(m_TargetMutex);
        m_PendingTarget = Target;
        m_HaveTarget = true;
    }
    // Departure + arrival can block on the display stack; never do it inline in the IOCTL.
    // The agent polls GET_STATUS for MonitorArrived / LastError.
    if (!m_ApplyWork)
    {
        m_ApplyWork = CreateThreadpoolWork(&FocusDeviceContext::ApplyTargetThunk, this, nullptr);
        if (!m_ApplyWork)
        {
            return STATUS_INSUFFICIENT_RESOURCES;
        }
    }
    SubmitThreadpoolWork(m_ApplyWork);
    return STATUS_PENDING;
}
void CALLBACK FocusDeviceContext::ApplyTargetThunk(PTP_CALLBACK_INSTANCE, PVOID Context, PTP_WORK)
{
    reinterpret_cast<FocusDeviceContext*>(Context)->ApplyPendingTarget();
}
void CALLBACK FocusDeviceContext::WatchdogThunk(PTP_CALLBACK_INSTANCE, PVOID Context, PTP_TIMER)
{
    reinterpret_cast<FocusDeviceContext*>(Context)->OnWatchdog();
}
void FocusDeviceContext::OnWatchdog()
{
    if (!m_MonitorArrived)
    {
        return;
    }
    const ULONGLONG Silence = GetTickCount64() - m_LastHeartbeatMs;
    if (Silence > FOCUSIDD_HEARTBEAT_TIMEOUT_MS)
    {
        RemoveMonitor();
    }
}
void FocusDeviceContext::ApplyPendingTarget()
{
    // One arrival/departure at a time; a second SET_TARGET queues behind the first.
    lock_guard<mutex> MonitorLock(m_MonitorMutex);
    FOCUSIDD_SET_TARGET Target;
    {
        lock_guard<mutex> Lock(m_TargetMutex);
        if (!m_HaveTarget)
        {
            return;
        }
        Target = m_PendingTarget;
    }
    if (!m_AdapterReady || !m_Adapter)
    {
        m_LastError = (UINT32)STATUS_DEVICE_NOT_READY;
        m_Writer.SetLastError((UINT32)STATUS_DEVICE_NOT_READY);
        return;
    }
    // Clamp to a refresh an EDID detailed timing can actually encode (16-bit, 10 kHz units).
    // The EFFECTIVE value is what the agent reads back in GET_STATUS, so the clamp is visible.
    const UINT32 Effective = EdidBuilder::MaxEncodableRefresh(Target.WidthPx, Target.HeightPx, Target.RefreshHz);
    if (Effective == 0)
    {
        m_LastError = (UINT32)STATUS_NOT_SUPPORTED;
        m_Writer.SetLastError((UINT32)STATUS_NOT_SUPPORTED);
        return;
    }
    Target.RefreshHz = Effective;
    // A live monitor's mode set cannot be edited in place; the supported sequence is
    // departure followed by a fresh arrival with a new descriptor. The stable serial /
    // container id make Windows treat it as the same panel being replugged.
    if (m_Monitor)
    {
        // Departure only. We do NOT WdfObjectDelete the monitor afterwards: Microsoft's
        // IddCxMonitorDeparture page does not say IddCx leaves ownership with the driver,
        // and a double delete in a driver is far worse than a small leak. The monitor
        // context is freed by the object's cleanup callback. (UNVERIFIED on real hardware.)
        IddCxMonitorDeparture(m_Monitor);
        m_Monitor = nullptr;
        m_MonitorArrived = false;
        m_Writer.SetMonitorArrived(false);
        Sleep(300); // let the OS finish processing the departure before the replug
    }
    EdidParams Params = {};
    Params.WidthPx = Target.WidthPx;
    Params.HeightPx = Target.HeightPx;
    Params.RefreshHz = Target.RefreshHz;
    Params.PhysWidthMm = Target.PhysWidthMm;
    Params.PhysHeightMm = Target.PhysHeightMm;
    Params.Serial = Target.Serial;
    StringCchCopyA(Params.Name, ARRAYSIZE(Params.Name), "FocusPhone");
    BYTE Edid[EdidBuilder::BlockSize];
    EdidBuilder::Build(Params, Edid);
    {
        lock_guard<mutex> Lock(m_TargetMutex);
        memcpy(m_Edid, Edid, sizeof(Edid));
        m_AppliedTarget = Target;
    }
    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, FocusMonitorContextWrapper);
    Attr.EvtCleanupCallback = [](WDFOBJECT Object)
    {
        auto* pWrapper = WdfObjectGet_FocusMonitorContextWrapper(Object);
        if (pWrapper)
        {
            pWrapper->Cleanup();
        }
    };
    IDDCX_MONITOR_INFO MonitorInfo = {};
    MonitorInfo.Size = sizeof(MonitorInfo);
    MonitorInfo.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER;
    MonitorInfo.ConnectorIndex = 0;
    MonitorInfo.MonitorDescription.Size = sizeof(MonitorInfo.MonitorDescription);
    MonitorInfo.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    MonitorInfo.MonitorDescription.DataSize = EdidBuilder::BlockSize;
    MonitorInfo.MonitorDescription.pData = m_Edid;
    // Stable container id derived from the phone serial so the same phone maps to the same
    // monitor identity across sessions (Windows keeps its per-monitor scale + position).
    MonitorInfo.MonitorContainerId = { 0xF0C05500, 0xA11C, 0x4D3A, { 0x9E, 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 } };
    MonitorInfo.MonitorContainerId.Data4[4] = (BYTE)(Target.Serial & 0xFF);
    MonitorInfo.MonitorContainerId.Data4[5] = (BYTE)((Target.Serial >> 8) & 0xFF);
    MonitorInfo.MonitorContainerId.Data4[6] = (BYTE)((Target.Serial >> 16) & 0xFF);
    MonitorInfo.MonitorContainerId.Data4[7] = (BYTE)((Target.Serial >> 24) & 0xFF);
    IDARG_IN_MONITORCREATE MonitorCreate = {};
    MonitorCreate.ObjectAttributes = &Attr;
    MonitorCreate.pMonitorInfo = &MonitorInfo;
    // The previous monitor may still be tearing down; retry creation briefly rather than
    // failing the whole session on a transient.
    IDARG_OUT_MONITORCREATE MonitorCreateOut = {};
    NTSTATUS Status = STATUS_UNSUCCESSFUL;
    for (int Attempt = 0; Attempt < 5; Attempt++)
    {
        Status = IddCxMonitorCreate(m_Adapter, &MonitorCreate, &MonitorCreateOut);
        if (NT_SUCCESS(Status))
        {
            break;
        }
        Sleep(300);
    }
    if (!NT_SUCCESS(Status))
    {
        m_LastError = (UINT32)Status;
        m_Writer.SetLastError((UINT32)Status);
        return;
    }
    auto* pMonitorContextWrapper = WdfObjectGet_FocusMonitorContextWrapper(MonitorCreateOut.MonitorObject);
    pMonitorContextWrapper->pContext = new FocusMonitorContext(MonitorCreateOut.MonitorObject, &m_Writer);
    m_Monitor = MonitorCreateOut.MonitorObject;
    IDARG_OUT_MONITORARRIVAL ArrivalOut;
    Status = IddCxMonitorArrival(m_Monitor, &ArrivalOut);
    if (!NT_SUCCESS(Status))
    {
        m_LastError = (UINT32)Status;
        m_Writer.SetLastError((UINT32)Status);
        // A created-but-never-arrived monitor is still our object to delete.
        WdfObjectDelete(m_Monitor);
        m_Monitor = nullptr;
        return;
    }
    m_MonitorArrived = true;
    m_LastError = 0;
    m_Writer.SetMonitorArrived(true);
    m_Writer.SetLastError(0);
    m_LastHeartbeatMs = GetTickCount64();
}
NTSTATUS FocusDeviceContext::RemoveMonitor()
{
    lock_guard<mutex> MonitorLock(m_MonitorMutex);
    if (m_Monitor)
    {
        IddCxMonitorDeparture(m_Monitor); // see ApplyPendingTarget: no WdfObjectDelete
        m_Monitor = nullptr;
    }
    m_MonitorArrived = false;
    m_Writer.SetMonitorArrived(false);
    return STATUS_SUCCESS;
}
void FocusDeviceContext::GetStatus(FOCUSIDD_STATUS* pStatus)
{
    memset(pStatus, 0, sizeof(*pStatus));
    pStatus->ProtocolVersion = FOCUSIDD_PROTOCOL_VERSION;
    pStatus->MonitorArrived = m_MonitorArrived ? 1 : 0;
    {
        lock_guard<mutex> Lock(m_TargetMutex);
        pStatus->WidthPx = m_AppliedTarget.WidthPx;
        pStatus->HeightPx = m_AppliedTarget.HeightPx;
        pStatus->RefreshHz = m_AppliedTarget.RefreshHz;
    }
    pStatus->FramesPublished = m_Writer.FramesPublished();
    pStatus->FramesDropped = m_Writer.FramesDropped();
    pStatus->LastError = m_LastError;
    pStatus->SurfaceWin32Error = m_Writer.CreateError();
}
#pragma endregion
#pragma region DDI Callbacks
_Use_decl_annotations_
NTSTATUS IddFocusAdapterInitFinished(IDDCX_ADAPTER AdapterObject, const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    auto* pDeviceContextWrapper = WdfObjectGet_FocusDeviceContextWrapper(AdapterObject);
    if (NT_SUCCESS(pInArgs->AdapterInitStatus) && pDeviceContextWrapper && pDeviceContextWrapper->pContext)
    {
        pDeviceContextWrapper->pContext->OnAdapterInitFinished();
    }
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS IddFocusAdapterCommitModes(IDDCX_ADAPTER AdapterObject, const IDARG_IN_COMMITMODES* pInArgs)
{
    UNREFERENCED_PARAMETER(AdapterObject);
    UNREFERENCED_PARAMETER(pInArgs);
    // Nothing to program: IddCx owns the swap chain and the committed mode is ground-truthed
    // by the acquired surface's descriptor in the processor thread (and by the agent).
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS IddFocusParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    // Exactly one monitor mode: the phone's. Offering a list would let Windows choose a
    // resolution the phone cannot present 1:1.
    pOutArgs->MonitorModeBufferOutputCount = 1;
    if (pInArgs->MonitorModeBufferInputCount < 1)
    {
        return (pInArgs->MonitorModeBufferInputCount > 0) ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    }
    if (!g_DeviceContext || pInArgs->MonitorDescription.DataSize != EdidBuilder::BlockSize)
    {
        return STATUS_INVALID_PARAMETER;
    }
    BYTE CurrentEdid[EdidBuilder::BlockSize];
    if (!g_DeviceContext->GetCurrentEdid(CurrentEdid) ||
        memcmp(pInArgs->MonitorDescription.pData, CurrentEdid, EdidBuilder::BlockSize) != 0)
    {
        return STATUS_INVALID_PARAMETER;
    }
    FOCUSIDD_SET_TARGET Target;
    if (!g_DeviceContext->GetCurrentTarget(&Target))
    {
        return STATUS_INVALID_PARAMETER;
    }
    pInArgs->pMonitorModes[0] = CreateMonitorMode(
        Target.WidthPx, Target.HeightPx, Target.RefreshHz, IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    pOutArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS IddFocusMonitorGetDefaultModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);
    // Only used if a monitor arrives with no/invalid EDID, which our flow never does.
    pOutArgs->DefaultMonitorModeBufferOutputCount = 1;
    if (pInArgs->DefaultMonitorModeBufferInputCount >= 1)
    {
        pInArgs->pDefaultMonitorModes[0] = CreateMonitorMode(1024, 768, 60, IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
        pOutArgs->PreferredMonitorModeIdx = 0;
    }
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS IddFocusMonitorQueryModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_QUERYTARGETMODES* pInArgs, IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);
    // Adapter processing capability. The OS intersects it with the monitor modes, and the
    // monitor advertises exactly one, so the session mode is forced to be the phone's.
    vector<IDDCX_TARGET_MODE> TargetModes;
    FOCUSIDD_SET_TARGET Target;
    if (g_DeviceContext && g_DeviceContext->GetCurrentTarget(&Target))
    {
        TargetModes.push_back(CreateTargetMode(Target.WidthPx, Target.HeightPx, Target.RefreshHz));
    }
    TargetModes.push_back(CreateTargetMode(2560, 1440, 120));
    TargetModes.push_back(CreateTargetMode(2560, 1440, 60));
    TargetModes.push_back(CreateTargetMode(1920, 1080, 120));
    TargetModes.push_back(CreateTargetMode(1920, 1080, 60));
    TargetModes.push_back(CreateTargetMode(1600, 900, 60));
    TargetModes.push_back(CreateTargetMode(1024, 768, 60));
    pOutArgs->TargetModeBufferOutputCount = (UINT)TargetModes.size();
    if (pInArgs->TargetModeBufferInputCount >= TargetModes.size())
    {
        copy(TargetModes.begin(), TargetModes.end(), pInArgs->pTargetModes);
    }
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS IddFocusMonitorAssignSwapChain(IDDCX_MONITOR MonitorObject, const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
    auto* pMonitorContextWrapper = WdfObjectGet_FocusMonitorContextWrapper(MonitorObject);
    pMonitorContextWrapper->pContext->AssignSwapChain(pInArgs->hSwapChain, pInArgs->RenderAdapterLuid, pInArgs->hNextSurfaceAvailable);
    return STATUS_SUCCESS;
}
_Use_decl_annotations_
NTSTATUS IddFocusMonitorUnassignSwapChain(IDDCX_MONITOR MonitorObject)
{
    auto* pMonitorContextWrapper = WdfObjectGet_FocusMonitorContextWrapper(MonitorObject);
    pMonitorContextWrapper->pContext->UnassignSwapChain();
    return STATUS_SUCCESS;
}
// [FOCUS] Custom control channel from the host agent.
_Use_decl_annotations_
VOID IddFocusDeviceIoControl(WDFDEVICE Device, WDFREQUEST Request, size_t OutputBufferLength, size_t InputBufferLength, ULONG IoControlCode)
{
    auto* pWrapper = WdfObjectGet_FocusDeviceContextWrapper(Device);
    FocusDeviceContext* pContext = pWrapper ? pWrapper->pContext : nullptr;
    if (!pContext)
    {
        WdfRequestCompleteWithInformation(Request, STATUS_DEVICE_NOT_READY, 0);
        return;
    }
    pContext->Touch(); // every IOCTL is a watchdog heartbeat
    NTSTATUS Status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR Information = 0;
    switch (IoControlCode)
    {
    case IOCTL_FOCUSIDD_PING:
    {
        UINT32* pVersion = nullptr;
        Status = WdfRequestRetrieveOutputBuffer(Request, sizeof(UINT32), (PVOID*)&pVersion, nullptr);
        if (NT_SUCCESS(Status))
        {
            *pVersion = FOCUSIDD_PROTOCOL_VERSION;
            Information = sizeof(UINT32);
        }
        break;
    }
    case IOCTL_FOCUSIDD_SET_TARGET:
    {
        FOCUSIDD_SET_TARGET* pTarget = nullptr;
        Status = WdfRequestRetrieveInputBuffer(Request, sizeof(FOCUSIDD_SET_TARGET), (PVOID*)&pTarget, nullptr);
        if (NT_SUCCESS(Status) && InputBufferLength >= sizeof(FOCUSIDD_SET_TARGET))
        {
            Status = pContext->SetTarget(*pTarget);
            if (Status == STATUS_PENDING)
            {
                Status = STATUS_SUCCESS; // accepted; the outcome is read via GET_STATUS
            }
        }
        break;
    }
    case IOCTL_FOCUSIDD_REMOVE_MONITOR:
        Status = pContext->RemoveMonitor();
        break;
    case IOCTL_FOCUSIDD_GET_STATUS:
    {
        FOCUSIDD_STATUS* pOut = nullptr;
        Status = WdfRequestRetrieveOutputBuffer(Request, sizeof(FOCUSIDD_STATUS), (PVOID*)&pOut, nullptr);
        if (NT_SUCCESS(Status) && OutputBufferLength >= sizeof(FOCUSIDD_STATUS))
        {
            pContext->GetStatus(pOut);
            Information = sizeof(FOCUSIDD_STATUS);
        }
        break;
    }
    default:
        break;
    }
    WdfRequestCompleteWithInformation(Request, Status, Information);
}
#pragma endregion
