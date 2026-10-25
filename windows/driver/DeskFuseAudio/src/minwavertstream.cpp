#include <DeskFuseAudio.h>
#include <limits.h>
#include <ks.h>
#include "simple.h"
#include "minwavert.h"
#include "minwavertstream.h"
#include "UnittestData.h"
#include "AudioModuleHelper.h"
#include "pcm_export.h"
#define MINWAVERTSTREAM_POOLTAG 'SRWM'

#pragma warning (disable : 4127)

//=============================================================================
// CMiniportWaveRTStream
//=============================================================================

//=============================================================================
#pragma code_seg("PAGE")
CMiniportWaveRTStream::~CMiniportWaveRTStream
( 
    void 
)
/*++

Routine Description:

  Destructor for wavertstream 

Arguments:

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();
    if (m_pNotificationTimer) {
        KIRQL irql;
        KeAcquireSpinLock(&m_PositionSpinLock, &irql);
        m_ShuttingDown = TRUE;
        m_KsState = KSSTATE_STOP;
        KeReleaseSpinLock(&m_PositionSpinLock, irql);
        ExDeleteTimer(m_pNotificationTimer, TRUE, TRUE, NULL);
        m_pNotificationTimer = NULL;
    }
    if (NULL != m_pMiniport)
    {
        if (m_pAudioModules)
        {
            m_pMiniport->FreeStreamAudioModules(m_pAudioModules, m_AudioModuleCount);
            m_pAudioModules = NULL;
            m_AudioModuleCount = 0;
        }
    
        if (m_bUnregisterStream)
        {
            m_pMiniport->StreamClosed(m_ulPin, this);
            m_bUnregisterStream = FALSE;
        }
        
        m_pMiniport->Release();
        m_pMiniport = NULL;
    }

    if (m_pDpc)
    {
        ExFreePoolWithTag( m_pDpc, MINWAVERTSTREAM_POOLTAG );
        m_pDpc = NULL;
    }

    if (m_pTimer)
    {
        ExFreePoolWithTag( m_pTimer, MINWAVERTSTREAM_POOLTAG );
        m_pTimer = NULL;
    }

    if (m_pbMuted)
    {
        ExFreePoolWithTag( m_pbMuted, MINWAVERTSTREAM_POOLTAG );
        m_pbMuted = NULL;
    }

    if (m_plVolumeLevel)
    {
        ExFreePoolWithTag( m_plVolumeLevel, MINWAVERTSTREAM_POOLTAG );
        m_plVolumeLevel = NULL;
    }

    if (m_plPeakMeter)
    {
        ExFreePoolWithTag( m_plPeakMeter, MINWAVERTSTREAM_POOLTAG );
        m_plPeakMeter = NULL;
    }

    if (m_pWfExt)
    {
        ExFreePoolWithTag( m_pWfExt, MINWAVERTSTREAM_POOLTAG );
        m_pWfExt = NULL;
    }

#ifdef SYSVAD_BTH_BYPASS
    ASSERT(m_SidebandOpen == FALSE);
    ASSERT(m_SidebandStarted == FALSE);
#endif  // SYSVAD_BTH_BYPASS

    DPF_ENTER(("[CMiniportWaveRTStream::~CMiniportWaveRTStream]"));
} // ~CMiniportWaveRTStream

//=============================================================================
#pragma code_seg("PAGE")

NTSTATUS CMiniportWaveRTStream::ReadRegistrySettings()
{
    PAGED_CODE();

    NTSTATUS                    ntStatus;
    PDRIVER_OBJECT              DriverObject;
    HANDLE                      DriverKey;

    RTL_QUERY_REGISTRY_TABLE    paramTable[] = {
        // QueryRoutine     Flags                                               Name                            EntryContext                            DefaultType                                                     DefaultData                                 DefaultLength
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"HostCaptureToneFrequency",        &m_ulHostCaptureToneFrequency,          (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_ulHostCaptureToneFrequency,              sizeof(DWORD) },
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"LoopbackCaptureToneFrequency",    &m_ulLoopbackCaptureToneFrequency,      (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_ulLoopbackCaptureToneFrequency,          sizeof(DWORD) },
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"HostCaptureToneAmplitude",        &m_dwHostCaptureToneAmplitude,          (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_dwHostCaptureToneAmplitude,              sizeof(DWORD) },
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"LoopbackCaptureToneAmplitude",    &m_dwLoopbackCaptureToneAmplitude,      (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_dwLoopbackCaptureToneAmplitude,          sizeof(DWORD) },
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"HostCaptureToneDCOffset",         &m_dwHostCaptureToneDCOffset,           (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_dwHostCaptureToneDCOffset,               sizeof(DWORD) },
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"LoopbackCaptureToneDCOffset",     &m_dwLoopbackCaptureToneDCOffset,       (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_dwLoopbackCaptureToneDCOffset,           sizeof(DWORD) },
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"HostCaptureToneInitialPhase",     &m_dwHostCaptureToneInitialPhase,       (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_dwHostCaptureToneInitialPhase,           sizeof(DWORD) },
        { NULL,   RTL_QUERY_REGISTRY_DIRECT | RTL_QUERY_REGISTRY_TYPECHECK, L"LoopbackCaptureToneInitialPhase", &m_dwLoopbackCaptureToneInitialPhase,   (REG_DWORD << RTL_QUERY_REGISTRY_TYPECHECK_SHIFT) | REG_DWORD,  &m_dwLoopbackCaptureToneInitialPhase,       sizeof(DWORD) },
        { NULL,   0,                                                        NULL,                               NULL,                                   0,                                                              NULL,                                       0 }
    };

    DriverObject = WdfDriverWdmGetDriverObject(WdfGetDriver());
    DriverKey = NULL;
    ntStatus = IoOpenDriverRegistryKey(DriverObject, 
                                 DriverRegKeyParameters,
                                 KEY_READ,
                                 0,
                                 &DriverKey);

    if (!NT_SUCCESS(ntStatus))
    {
        return ntStatus;
    }

    ntStatus = RtlQueryRegistryValues(RTL_REGISTRY_HANDLE,
                                  (PCWSTR) DriverKey,
                                  &paramTable[0],
                                  NULL,
                                  NULL);

    if (!NT_SUCCESS(ntStatus)) 
    {
        DPF(D_VERBOSE, ("RtlQueryRegistryValues failed, using default values, 0x%x", ntStatus));
        //
        // Don't return error because we will operate with default values.
        //
    }

    if (DriverKey)
    {
        ZwClose(DriverKey);
    }

    return ntStatus;
}

NTSTATUS
CMiniportWaveRTStream::Init
( 
    _In_ PCMiniportWaveRT           Miniport_,
    _In_ PPORTWAVERTSTREAM          PortStream_,
    _In_ ULONG                      Pin_,
    _In_ BOOLEAN                    Capture_,
    _In_ PKSDATAFORMAT              DataFormat_,
    _In_ GUID                       SignalProcessingMode
)
/*++

Routine Description:

  Initializes the stream object.

Arguments:

  Miniport_ -

  Pin_ -

  Capture_ -

  DataFormat -

  SignalProcessingMode - The driver uses the signalProcessingMode to configure
    driver and/or hardware specific signal processing to be applied to this new
    stream.

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();

    PWAVEFORMATEX pWfEx = NULL;
    NTSTATUS ntStatus = STATUS_SUCCESS;

    m_pMiniport = NULL;
    m_ulPin = 0;
    m_bUnregisterStream = FALSE;
    m_bCapture = FALSE;
    m_ulDmaBufferSize = 0;
    m_pDmaBuffer = NULL;
    m_ulNotificationsPerBuffer = 0;
    m_KsState = KSSTATE_STOP;
    m_pTimer = NULL;
    m_pDpc = NULL;
    m_llPacketCounter = 0;
    m_ullPlayPosition = 0;
    m_ullWritePosition = 0;
    m_ullDmaTimeStamp = 0;
    m_hnsElapsedTimeCarryForward = 0;
    m_ullLastDPCTimeStamp = 0;
    m_hnsDPCTimeCarryForward = 0;
    m_ulDmaMovementRate = 0;
    m_byteDisplacementCarryForward = 0;
    m_bLfxEnabled = FALSE;
    m_pbMuted = NULL;
    m_plVolumeLevel = NULL;
    m_plPeakMeter = NULL;
    m_pWfExt = NULL;
    m_ullLinearPosition = 0;
    m_ullPresentationPosition = 0;
    m_ulContentId = 0;
    m_ulCurrentWritePosition = 0;
    m_ulLastOsReadPacket = ULONG_MAX;
    m_ulLastOsWritePacket = ULONG_MAX;
    m_HasLastWritePacket = FALSE;
    m_IsCurrentWritePositionUpdated = 0;
    m_SignalProcessingMode = SignalProcessingMode;
    m_bEoSReceived = FALSE;
    m_bLastBufferRendered = FALSE;
    m_pAudioModules = NULL;
    m_AudioModuleCount = 0;

    m_ulHostCaptureToneFrequency = IsEqualGUID(SignalProcessingMode, AUDIO_SIGNALPROCESSINGMODE_RAW) ? 1000 : 2000;
    m_ulLoopbackCaptureToneFrequency = 3000; // 3 kHz
    m_dwHostCaptureToneAmplitude = 50; 
    m_dwLoopbackCaptureToneAmplitude = 50; 
    m_dwHostCaptureToneDCOffset = 0; 
    m_dwLoopbackCaptureToneDCOffset = 0; 
    m_dwHostCaptureToneInitialPhase = 0; 
    m_dwLoopbackCaptureToneInitialPhase = 0; 


#if defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)
    m_SidebandOpen = FALSE;
    m_SidebandStarted = FALSE;
#endif  // defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)

    m_pPortStream = PortStream_;
    InitializeListHead(&m_NotificationList);
    m_PacketFrames = 0;
    m_FrameClock = {};
    m_LastNotifiedPacket = m_MissedNotifications = m_EosEndFrame = 0;
    m_EosNotified = m_ShuttingDown = FALSE;

    // Initialize the spinlock to synchronize position updates
    KeInitializeSpinLock(&m_PositionSpinLock);

    m_pNotificationTimer = ExAllocateTimer(
         TimerNotifyRT,
         this,
         EX_TIMER_HIGH_RESOLUTION
    );
    if (!m_pNotificationTimer)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    pWfEx = GetWaveFormatEx(DataFormat_);
    if (NULL == pWfEx) 
    { 
        return STATUS_UNSUCCESSFUL; 
    }

    m_pMiniport = reinterpret_cast<CMiniportWaveRT*>(Miniport_);
    if (m_pMiniport == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }
    m_pMiniport->AddRef();
    if (!NT_SUCCESS(ntStatus))
    {
        return ntStatus;
    }
    m_ulPin = Pin_;
    m_bCapture = Capture_;
    m_ulDmaMovementRate = pWfEx->nAvgBytesPerSec;

    m_pDpc = (PRKDPC)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(KDPC), MINWAVERTSTREAM_POOLTAG);
    if (!m_pDpc)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    m_pWfExt = (PWAVEFORMATEXTENSIBLE)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(WAVEFORMATEX) + pWfEx->cbSize, MINWAVERTSTREAM_POOLTAG);
    if (m_pWfExt == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    RtlCopyMemory(m_pWfExt, pWfEx, sizeof(WAVEFORMATEX) + pWfEx->cbSize);

    m_pbMuted = (PBOOL)ExAllocatePool2(POOL_FLAG_NON_PAGED, m_pWfExt->Format.nChannels * sizeof(BOOL), MINWAVERTSTREAM_POOLTAG);
    if (m_pbMuted == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    m_plVolumeLevel = (PLONG)ExAllocatePool2(POOL_FLAG_NON_PAGED, m_pWfExt->Format.nChannels * sizeof(LONG), MINWAVERTSTREAM_POOLTAG);
    if (m_plVolumeLevel == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    m_plPeakMeter = (PLONG)ExAllocatePool2(POOL_FLAG_NON_PAGED, m_pWfExt->Format.nChannels * sizeof(LONG), MINWAVERTSTREAM_POOLTAG);
    if (m_plPeakMeter == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    //
    // Allocate stream audio module resources.
    //
    ntStatus = m_pMiniport->AllocStreamAudioModules(&SignalProcessingMode,
                                                    &m_pAudioModules,
                                                    &m_AudioModuleCount);
    if (!NT_SUCCESS(ntStatus))
    {
        return ntStatus;
    }

    if (m_bCapture)
    {
        ReadRegistrySettings();
            DWORD toneFrequency = 0;
            DWORD toneAmplitude = 0;
            DWORD toneDCOffset = 0;
            DWORD toneInitialPhase = 0;

            double toneAmplitudeDouble = 0;
            double toneDCOffsetDouble = 0;
            double toneInitialPhaseDouble = 0;

            if (m_pMiniport->IsLoopbackPin(Pin_))
            {
                //
                // Loopbacks pins use a different frequency for test validation.
                //
                toneFrequency = m_ulLoopbackCaptureToneFrequency;
                toneAmplitude = m_dwLoopbackCaptureToneAmplitude;
                toneDCOffset  = m_dwLoopbackCaptureToneDCOffset;
                toneInitialPhase = m_dwLoopbackCaptureToneInitialPhase;
            }
            else
            {
                //
                // Init sine wave generator. To exercise the SignalProcessingMode parameter
                // this sample driver selects the frequency based on the parameter.
                //
                toneFrequency = m_ulHostCaptureToneFrequency;
                toneAmplitude = m_dwHostCaptureToneAmplitude;
                toneDCOffset  = m_dwHostCaptureToneDCOffset;
                toneInitialPhase = m_dwHostCaptureToneInitialPhase;
            }

            if (labs(toneAmplitude) > 100)
            {
                toneAmplitude = toneAmplitude > 0 ? 100 : -100;
            }

            if (labs(toneDCOffset) > 100)
            {
                toneDCOffset = toneDCOffset > 0 ? 100 : -100;
            }

            DWORD abssum = labs(toneAmplitude) + labs(toneDCOffset);
            
            if ( abssum > 100)
            {
                toneAmplitudeDouble = ((double)toneAmplitude) / abssum;
                toneDCOffsetDouble = ((double)toneDCOffset) / abssum;
            }
            else
            {
                toneAmplitudeDouble = ((double)toneAmplitude) / 100.0;
                toneDCOffsetDouble = ((double)toneDCOffset) / 100.0;
            }

            if (labs(toneInitialPhase) > 31416)
            {
                toneInitialPhase = toneInitialPhase > 0 ? 31416 : -31416;
            }

            toneInitialPhaseDouble = (double)toneInitialPhase / 10000;

            ntStatus = m_ToneGenerator.Init(toneFrequency, toneAmplitudeDouble, toneDCOffsetDouble, toneInitialPhaseDouble, m_pWfExt);
        if (!NT_SUCCESS(ntStatus))
        {
            return ntStatus;
        }
    }

    //
    // Register this stream.
    //
    ntStatus = m_pMiniport->StreamCreated(m_ulPin, this);
    if (NT_SUCCESS(ntStatus))
    {
        m_bUnregisterStream = TRUE;
    }

    return ntStatus;
} // Init

//=============================================================================
#pragma code_seg("PAGE")
STDMETHODIMP_(NTSTATUS)
CMiniportWaveRTStream::NonDelegatingQueryInterface
( 
    _In_ REFIID  Interface,
    _COM_Outptr_ PVOID * Object 
)
/*++

Routine Description:

  QueryInterface

Arguments:

  Interface - GUID

  Object - interface pointer to be returned

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();

    ASSERT(Object);

    if (IsEqualGUIDAligned(Interface, IID_IUnknown))
    {
        *Object = PVOID(PUNKNOWN(PMINIPORTWAVERTSTREAM(this)));
    }
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTStream))
    {
        *Object = PVOID(PMINIPORTWAVERTSTREAM(this));
    }
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTStreamNotification))
    {
        *Object = PVOID(PMINIPORTWAVERTSTREAMNOTIFICATION(this));
    }
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTInputStream) && (this->m_bCapture))
    {
        // This interface is supported only on capture streams
        *Object = PVOID(PMINIPORTWAVERTINPUTSTREAM(this));
    }
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportWaveRTOutputStream) && (!this->m_bCapture)
        && (!this->m_pMiniport->IsOffloadPin(this->m_ulPin)))
    {
        // This interface is supported only on host render streams
        *Object = PVOID(PMINIPORTWAVERTOUTPUTSTREAM(this));
    }
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportStreamAudioEngineNode))
    {
        *Object = (PVOID)(IMiniportStreamAudioEngineNode*)this;
    }
    else if (IsEqualGUIDAligned(Interface, IID_IMiniportStreamAudioEngineNode2))
    {
        *Object = (PVOID)(IMiniportStreamAudioEngineNode2*)this;
    }
    else if (IsEqualGUIDAligned(Interface, IID_IDrmAudioStream))
    {
        *Object = (PVOID)(IDrmAudioStream*)this;
    }
    else
    {
        *Object = NULL;
    }

    if (*Object)
    {
        PUNKNOWN(*Object)->AddRef();
        return STATUS_SUCCESS;
    }

    return STATUS_INVALID_PARAMETER;
} // NonDelegatingQueryInterface

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS CMiniportWaveRTStream::AllocateBufferWithNotification
(
    _In_    ULONG               NotificationCount_,
    _In_    ULONG               RequestedSize_,
    _Out_   PMDL                *AudioBufferMdl_,
    _Out_   ULONG               *ActualSize_,
    _Out_   ULONG               *OffsetFromFirstPage_,
    _Out_   MEMORY_CACHING_TYPE *CacheType_
)
{
    PAGED_CODE();


    if ( (0 == RequestedSize_) || (RequestedSize_ < m_pWfExt->Format.nBlockAlign) )
    { 
        return STATUS_UNSUCCESSFUL; 
    }
    
    if (!NotificationCount_ || RequestedSize_ % m_pWfExt->Format.nBlockAlign ||
        (RequestedSize_ / m_pWfExt->Format.nBlockAlign) % NotificationCount_)
    {
        return STATUS_INVALID_PARAMETER;
    }

    RequestedSize_ -= RequestedSize_ % (m_pWfExt->Format.nBlockAlign);
    
    PHYSICAL_ADDRESS highAddress;
    highAddress.HighPart = 0;
    highAddress.LowPart = MAXULONG;

    PMDL pBufferMdl = m_pPortStream->AllocatePagesForMdl (highAddress, RequestedSize_);

    if (NULL == pBufferMdl)
    {
        return STATUS_UNSUCCESSFUL;
    }

    // From MSDN: 
    // "Since the Windows audio stack does not support a mechanism to express memory access 
    //  alignment requirements for buffers, audio drivers must select a caching type for mapped
    //  memory buffers that does not impose platform-specific alignment requirements. In other 
    //  words, the caching type used by the audio driver for mapped memory buffers, must not make 
    //  assumptions about the memory alignment requirements for any specific platform.
    //
    //  This method maps the physical memory pages in the MDL into kernel-mode virtual memory. 
    //  Typically, the miniport driver calls this method if it requires software access to the 
    //  scatter-gather list for an audio buffer. In this case, the storage for the scatter-gather 
    //  list must have been allocated by the IPortWaveRTStream::AllocatePagesForMdl or 
    //  IPortWaveRTStream::AllocateContiguousPagesForMdl method. 
    //
    //  A WaveRT miniport driver should not require software access to the audio buffer itself."
    //   
    m_pDmaBuffer = (BYTE*)m_pPortStream->MapAllocatedPages(pBufferMdl, MmCached);
    m_ulNotificationsPerBuffer = NotificationCount_;
    m_ulDmaBufferSize = RequestedSize_;
    m_PacketFrames = RequestedSize_ / m_pWfExt->Format.nBlockAlign / NotificationCount_;
    if (!m_pDmaBuffer) {
        m_pPortStream->FreePagesFromMdl(pBufferMdl);
        m_ulDmaBufferSize = m_ulNotificationsPerBuffer = m_PacketFrames = 0;
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    *AudioBufferMdl_ = pBufferMdl;
    *ActualSize_ = RequestedSize_;
    *OffsetFromFirstPage_ = 0;
    *CacheType_ = MmCached;

    return STATUS_SUCCESS;
}

//=============================================================================
#pragma code_seg("PAGE")
VOID CMiniportWaveRTStream::FreeBufferWithNotification
(
    _In_        PMDL    Mdl_,
    _In_        ULONG   Size_
)
{
    UNREFERENCED_PARAMETER(Size_);

    PAGED_CODE();

    // Serialize with the callback before unmapping its source storage. A
    // cancelled callback may still enter, but observes STOP and touches no PCM.
    KIRQL irql;
    KeAcquireSpinLock(&m_PositionSpinLock, &irql);
    m_KsState = KSSTATE_STOP;
    m_PacketFrames = 0;
    ExCancelTimer(m_pNotificationTimer, NULL);
    KeReleaseSpinLock(&m_PositionSpinLock, irql);

    if (Mdl_ != NULL)
    {
        if (m_pDmaBuffer != NULL)
        {
            m_pPortStream->UnmapAllocatedPages(m_pDmaBuffer, Mdl_);
            m_pDmaBuffer = NULL;
        }
        
        m_pPortStream->FreePagesFromMdl(Mdl_);
    }
    
    m_ulDmaBufferSize = 0;
    m_ulNotificationsPerBuffer = 0;

    return;
}

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS CMiniportWaveRTStream::RegisterNotificationEvent
(
    _In_ PKEVENT NotificationEvent_
)
{
    UNREFERENCED_PARAMETER(NotificationEvent_);

    PAGED_CODE();

    NotificationListEntry *nleNew = (NotificationListEntry*)ExAllocatePool2( 
        POOL_FLAG_NON_PAGED,
        sizeof(NotificationListEntry),
        MINWAVERTSTREAM_POOLTAG);
    if (NULL == nleNew)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    nleNew->NotificationEvent = NotificationEvent_;

    KIRQL irql;
    KeAcquireSpinLock(&m_PositionSpinLock,&irql);
    for (auto e=m_NotificationList.Flink;e!=&m_NotificationList;e=e->Flink) {
        auto n=CONTAINING_RECORD(e,NotificationListEntry,ListEntry);
        if (n->NotificationEvent==NotificationEvent_) {
            KeReleaseSpinLock(&m_PositionSpinLock,irql);ExFreePoolWithTag(nleNew,MINWAVERTSTREAM_POOLTAG);
            return STATUS_INVALID_PARAMETER;
        }
    }
    InsertTailList(&m_NotificationList,&nleNew->ListEntry);
    KeReleaseSpinLock(&m_PositionSpinLock,irql);
    return STATUS_SUCCESS;
}

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS CMiniportWaveRTStream::UnregisterNotificationEvent
(
    _In_ PKEVENT NotificationEvent_
)
{
    UNREFERENCED_PARAMETER(NotificationEvent_);

    PAGED_CODE();

    NotificationListEntry* removed=nullptr;
    KIRQL irql;KeAcquireSpinLock(&m_PositionSpinLock,&irql);
    for (auto e=m_NotificationList.Flink;e!=&m_NotificationList;e=e->Flink) {
        auto n=CONTAINING_RECORD(e,NotificationListEntry,ListEntry);
        if (n->NotificationEvent==NotificationEvent_) {RemoveEntryList(e);removed=n;break;}
    }
    KeReleaseSpinLock(&m_PositionSpinLock,irql);
    if (!removed) return STATUS_NOT_FOUND;
    ExFreePoolWithTag(removed,MINWAVERTSTREAM_POOLTAG);
    return STATUS_SUCCESS;
}

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS CMiniportWaveRTStream::GetClockRegister
(
    _Out_ PKSRTAUDIO_HWREGISTER Register_
)
{
    UNREFERENCED_PARAMETER(Register_);

    PAGED_CODE();

    return STATUS_NOT_IMPLEMENTED;
}

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS CMiniportWaveRTStream::GetPositionRegister
(
    _Out_ PKSRTAUDIO_HWREGISTER Register_
)
{
    UNREFERENCED_PARAMETER(Register_);

    PAGED_CODE();

    return STATUS_NOT_IMPLEMENTED;
}

//=============================================================================
#pragma code_seg("PAGE")
VOID CMiniportWaveRTStream::GetHWLatency
(
    _Out_ PKSRTAUDIO_HWLATENCY  Latency_
)
{
    PAGED_CODE();

    ASSERT(Latency_);

    Latency_->ChipsetDelay = 0;
    Latency_->CodecDelay = 0;
    Latency_->FifoSize = 0;
}

//=============================================================================
#pragma code_seg("PAGE")
VOID CMiniportWaveRTStream::FreeAudioBuffer
(
_In_opt_    PMDL        Mdl_,
_In_        ULONG       Size_
)
{
    UNREFERENCED_PARAMETER(Size_);

    PAGED_CODE();

    if (Mdl_ != NULL)
    {
        if (m_pDmaBuffer != NULL)
        {
            m_pPortStream->UnmapAllocatedPages(m_pDmaBuffer, Mdl_);
            m_pDmaBuffer = NULL;
        }

        m_pPortStream->FreePagesFromMdl(Mdl_);
    }

    m_ulDmaBufferSize = 0;
    m_ulNotificationsPerBuffer = 0;
}

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS CMiniportWaveRTStream::AllocateAudioBuffer
(
_In_    ULONG                   RequestedSize_,
_Out_   PMDL                   *AudioBufferMdl_,
_Out_   ULONG                  *ActualSize_,
_Out_   ULONG                  *OffsetFromFirstPage_,
_Out_   MEMORY_CACHING_TYPE    *CacheType_
)
{
    PAGED_CODE();

    if ((0 == RequestedSize_) || (RequestedSize_ < m_pWfExt->Format.nBlockAlign))
    {
        return STATUS_UNSUCCESSFUL;
    }

    RequestedSize_ -= RequestedSize_ % (m_pWfExt->Format.nBlockAlign);

    PHYSICAL_ADDRESS highAddress;
    highAddress.HighPart = 0;
    highAddress.LowPart = MAXULONG;

    PMDL pBufferMdl = m_pPortStream->AllocatePagesForMdl(highAddress, RequestedSize_);

    if (NULL == pBufferMdl)
    {
        return STATUS_UNSUCCESSFUL;
    }

    // From MSDN: 
    // "Since the Windows audio stack does not support a mechanism to express memory access 
    //  alignment requirements for buffers, audio drivers must select a caching type for mapped
    //  memory buffers that does not impose platform-specific alignment requirements. In other 
    //  words, the caching type used by the audio driver for mapped memory buffers, must not make 
    //  assumptions about the memory alignment requirements for any specific platform.
    //
    //  This method maps the physical memory pages in the MDL into kernel-mode virtual memory. 
    //  Typically, the miniport driver calls this method if it requires software access to the 
    //  scatter-gather list for an audio buffer. In this case, the storage for the scatter-gather 
    //  list must have been allocated by the IPortWaveRTStream::AllocatePagesForMdl or 
    //  IPortWaveRTStream::AllocateContiguousPagesForMdl method. 
    //
    //  A WaveRT miniport driver should not require software access to the audio buffer itself."
    //   
    m_pDmaBuffer = (BYTE*)m_pPortStream->MapAllocatedPages(pBufferMdl, MmCached);

    m_ulDmaBufferSize = RequestedSize_;
    m_ulNotificationsPerBuffer = 0;

    *AudioBufferMdl_ = pBufferMdl;
    *ActualSize_ = RequestedSize_;
    *OffsetFromFirstPage_ = 0;
    *CacheType_ = MmCached;

    return STATUS_SUCCESS;
}

//=============================================================================
#pragma code_seg()
NTSTATUS CMiniportWaveRTStream::GetPosition
(
    _Out_   KSAUDIO_POSITION    *Position_
)
{
    NTSTATUS ntStatus;

#if defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)
    if (m_SidebandStarted)
    {
        ntStatus = GetSidebandStreamNtStatus();
        IF_FAILED_JUMP(ntStatus, Done);
    }
#endif // defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)

    // Return failure if this is the keyword detector pin
    if (m_pMiniport->IsKeywordDetectorPin(m_ulPin))
    {
        return STATUS_NOT_SUPPORTED;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_PositionSpinLock, &oldIrql);

    if (m_KsState == KSSTATE_RUN)
    {
        //
        // Get the current time and update position.
        //
        LARGE_INTEGER ilQPC = KeQueryPerformanceCounter(NULL);
        UpdatePosition(ilQPC);
    }

    Position_->PlayOffset = m_ullPlayPosition;
    Position_->WriteOffset = m_ullWritePosition;

    KeReleaseSpinLock(&m_PositionSpinLock, oldIrql);

    ntStatus = STATUS_SUCCESS;
    
#if defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)
Done:
#endif // defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)
    return ntStatus;
}

//=============================================================================
// CMiniportWaveRTStream::GetReadPacket
//
//  Returns information about the next packet for the OS to read.
//
// Return value
//
//  Returns STATUS_DEVICE_NOT_READY if no new packets are available.
//
// IRQL - PASSIVE_LEVEL
//
// Remarks
//  Although called at passive level, this routine is non-paged code because
//  it is called in the streaming path where page faults should be avoided.
//
// ISSUE-2014/10/4 Will this work correctly across pause/play?
#pragma code_seg()
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS CMiniportWaveRTStream::GetReadPacket
(
    _Out_ ULONG     *PacketNumber,
    _Out_ DWORD     *Flags,
    _Out_ ULONG64   *PerformanceCounterValue,
    _Out_ BOOL      *MoreData
)
{
    NTSTATUS ntStatus;
    ULONG availablePacketNumber;
    ULONG droppedPackets;

    // The call must be from event driven mode
    if(m_ulNotificationsPerBuffer == 0)
    {
        return STATUS_NOT_SUPPORTED;
    }
    
    *Flags = 0;

    if (m_KsState < KSSTATE_PAUSE)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    // If this is the keyword detector pin, then stream from the keyword FIFO
    if (m_pMiniport->IsKeywordDetectorPin(m_ulPin))
    {
        // FUTURE-2014/11/18 Drive this with packet counter
        ntStatus = m_pMiniport->m_KeywordDetector.GetReadPacket(m_ulNotificationsPerBuffer, m_ulDmaBufferSize, m_pDmaBuffer, PacketNumber, PerformanceCounterValue, MoreData);
        if (NT_SUCCESS(ntStatus))
        {
            m_ulLastOsReadPacket = *PacketNumber;
        }
        return ntStatus;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_PositionSpinLock, &oldIrql);

    if (m_KsState == KSSTATE_RUN) UpdatePosition(KeQueryPerformanceCounter(NULL));
    LONGLONG packetCounter = m_llPacketCounter;
    ULONGLONG ullLinearPosition = m_ullLinearPosition;
    ULONGLONG hnsElapsedTimeCarryForward = m_hnsElapsedTimeCarryForward;
    ULONGLONG ullDmaTimeStamp = m_ullDmaTimeStamp;

    KeReleaseSpinLock(&m_PositionSpinLock, oldIrql);

    // The 0-based number of the last completed packet
    // FUTURE-2014/10/27 Update to allow different numbers of packets per WaveRT buffer
    availablePacketNumber = LODWORD(packetCounter - 1);  // Note this might be ULONG_MAX if called during the first packet

    // If no new packets are available...
    if (availablePacketNumber == m_ulLastOsReadPacket)
    {
        return STATUS_DEVICE_NOT_READY;
    }

    // If more than one packet has transferred since the last packet read by
    // the OS, then those were dropped. That is, a glitch occurred.
    droppedPackets = availablePacketNumber - m_ulLastOsReadPacket - 1;
    if (droppedPackets > 0)
    {
        // Trace a glitch
    }

    // Return next packet number to be read
    *PacketNumber = availablePacketNumber;

    // Compute and return timestamp corresponding to the end of the available packet. In a real hardware
    // driver, the timestamp would be computed in a driver and hardware specific manner. In this sample
    // driver, it is extrapolated from the sample driver's internal simulated position correlation
    // [m_ullLinearPosition @ m_ullDmaTimeStamp] and the sample's internal 64-bit packet counter, subtracting
    // 1 from the packet counter to compute the time at the start of that last completed packet.
    ULONGLONG linearPositionOfAvailablePacket = packetCounter * (m_ulDmaBufferSize / m_ulNotificationsPerBuffer);
    // Need to divide by (1000 * 10000 because m_ulDmaMovementRate is average bytes per sec
    ULONGLONG carryForwardBytes = (hnsElapsedTimeCarryForward * m_ulDmaMovementRate) / 10000000;
    ULONGLONG deltaLinearPosition = ullLinearPosition + carryForwardBytes - linearPositionOfAvailablePacket;
    ULONGLONG deltaTimeInHns = deltaLinearPosition * 10000000 / m_ulDmaMovementRate;
    ULONGLONG timeOfAvailablePacketInHns = ullDmaTimeStamp - deltaTimeInHns;
    ULONGLONG timeOfAvailablePacketInQpc = timeOfAvailablePacketInHns * m_ullPerformanceCounterFrequency.QuadPart / 10000000;

    *PerformanceCounterValue = timeOfAvailablePacketInQpc;

    // No flags are defined yet
    *Flags = 0;

    // This sample does not internally buffer data so there is never more data
    // than revealed by the results from this routine.
    *MoreData = FALSE;

    // Update the last packet read by the OS
    m_ulLastOsReadPacket = availablePacketNumber;

    return STATUS_SUCCESS;
}

#pragma code_seg()
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS CMiniportWaveRTStream::SetWritePacket(
    _In_ ULONG PacketNumber, _In_ DWORD Flags, _In_ ULONG EosPacketLength)
{
    if (!m_ulNotificationsPerBuffer) return STATUS_NOT_SUPPORTED;
    if (Flags & ~KSSTREAM_HEADER_OPTIONSF_ENDOFSTREAM) return STATUS_INVALID_PARAMETER;
    DeskFusePcmReservation reservation={};
    bool publish=false;
    ULONG exportFrames=0, exportOffset=0;
    KIRQL irql;
    KeAcquireSpinLock(&m_PositionSpinLock, &irql);
    NTSTATUS status = STATUS_SUCCESS;
    const auto now = KeQueryPerformanceCounter(NULL);
    if (m_KsState == KSSTATE_RUN) UpdatePosition(now);
    const ULONG expected = static_cast<ULONG>(m_llPacketCounter) + (m_KsState == KSSTATE_RUN);
    const LONG delta = static_cast<LONG>(PacketNumber - expected);
    const auto packetBytes = m_PacketFrames * m_pWfExt->Format.nBlockAlign;
    const bool eos = (Flags & KSSTREAM_HEADER_OPTIONSF_ENDOFSTREAM) != 0;
    if (m_ShuttingDown || m_KsState < KSSTATE_PAUSE || m_bEoSReceived)
        status = STATUS_INVALID_DEVICE_STATE;
    else if (delta < 0 || (m_HasLastWritePacket && PacketNumber == m_ulLastOsWritePacket))
        status = STATUS_DATA_LATE_ERROR;
    else if (delta > 0) status = STATUS_DATA_OVERRUN;
    else if (eos && (EosPacketLength > packetBytes || EosPacketLength % m_pWfExt->Format.nBlockAlign))
        status = STATUS_INVALID_PARAMETER;
    else {
        auto position = (PacketNumber % m_ulNotificationsPerBuffer) * packetBytes;
        status = SetCurrentWritePositionInternal(position + (eos ? EosPacketLength : 0));
        if (NT_SUCCESS(status)) {
            m_ulLastOsWritePacket = PacketNumber;
            m_HasLastWritePacket = TRUE;
            exportFrames=(eos?EosPacketLength:packetBytes)/m_pWfExt->Format.nBlockAlign;
            exportOffset=position;
            const auto first=(static_cast<ULONGLONG>(m_llPacketCounter)+(m_KsState==KSSTATE_RUN))*m_PacketFrames;
            if (m_ulNotificationsPerBuffer==2) {
                publish=DeskFusePcmReserve(m_pMiniport->m_PcmSource,this,first,exportFrames,
                    deskfuse::mul_div(now.QuadPart,10000000,m_ullPerformanceCounterFrequency.QuadPart),&reservation);
            } else DeskFusePcmDeny(m_pMiniport->m_PcmSource);
            if (eos) {
                // Extend the wrapped packet number using the current 64-bit clock.
                m_EosEndFrame = (static_cast<ULONGLONG>(m_llPacketCounter) +
                    (m_KsState == KSSTATE_RUN)) * m_PacketFrames +
                    EosPacketLength / m_pWfExt->Format.nBlockAlign;
                m_bEoSReceived = TRUE;
                ArmNotificationLocked();
            }
        }
    }
    // Source ownership is the two-packet notification protocol, not just this
    // call's stack lifetime. The accepted packet is ahead of consumption and
    // differs from the current cyclic packet. Keep position/count queries AND
    // completion-event publication excluded until the bounded copy finishes:
    // Windows cannot be told that this packet has completed and reuse it.
    // No position/clock register is mapped; GetPosition/GetPacketCount and the
    // timer all use this lock. Default PortCls serialization also excludes
    // state changes/unmapping and other SetWritePacket calls. Unsupported
    // notification geometry is denied above rather than assuming ownership.
    // The OS shared-engine producer must obey WaveRT valid-packet ownership;
    // a client writing arbitrary shared bytes outside that protocol is unsupported.
    if (publish) DeskFusePcmPublish(m_pMiniport->m_PcmSource,reservation,m_pDmaBuffer+exportOffset,exportFrames);
    KeReleaseSpinLock(&m_PositionSpinLock, irql);
    return status;
}

//=============================================================================
#pragma code_seg()
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS CMiniportWaveRTStream::GetOutputStreamPresentationPosition
(
    _Out_ KSAUDIO_PRESENTATION_POSITION *pPresentationPosition
)
{
    ASSERT (pPresentationPosition);
    
    // The call must be from event driven mode
    if(m_ulNotificationsPerBuffer == 0)
    {
        return STATUS_NOT_SUPPORTED;
    }
    
    return GetPresentationPosition(pPresentationPosition);
}

//=============================================================================
#pragma code_seg()
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS CMiniportWaveRTStream::GetPacketCount
(
    _Out_ ULONG *pPacketCount
)
{
    ASSERT(pPacketCount);

    // The call must be from event driven mode
    if(m_ulNotificationsPerBuffer == 0)
    {
        return STATUS_NOT_SUPPORTED;
    }
    
    KIRQL oldIrql;
    KeAcquireSpinLock(&m_PositionSpinLock, &oldIrql);

    if (m_KsState == KSSTATE_RUN)
    {
        // Get the current time and update simulated position.
        LARGE_INTEGER ilQPC = KeQueryPerformanceCounter(NULL);
        UpdatePosition(ilQPC);
    }

    *pPacketCount = LODWORD(m_llPacketCounter);
    KeReleaseSpinLock(&m_PositionSpinLock, oldIrql);

    return STATUS_SUCCESS;
}

//=============================================================================
#pragma code_seg()
NTSTATUS CMiniportWaveRTStream::SetState(_In_ KSSTATE State_)
{
    if (State_ < KSSTATE_STOP || State_ > KSSTATE_RUN) return STATUS_INVALID_PARAMETER;
    KIRQL irql;
    KeAcquireSpinLock(&m_PositionSpinLock, &irql);
    if (m_ShuttingDown || (State_ == KSSTATE_RUN && !m_ulDmaBufferSize)) {
        KeReleaseSpinLock(&m_PositionSpinLock, irql);
        return STATUS_INVALID_DEVICE_STATE;
    }
    const auto previous = m_KsState;
    const auto now = KeQueryPerformanceCounter(&m_ullPerformanceCounterFrequency);
    if (previous == KSSTATE_RUN) UpdatePosition(now);
    m_KsState = State_; // callback observes non-RUN before cancellation
    if (State_ == KSSTATE_STOP) {
        m_FrameClock.reset();
        m_llPacketCounter = 0;
        m_ullPlayPosition = m_ullWritePosition = 0;
        m_ullLinearPosition = m_ullPresentationPosition = 0;
        m_LastNotifiedPacket = m_MissedNotifications = m_EosEndFrame = 0;
        m_bEoSReceived = m_bLastBufferRendered = m_EosNotified = FALSE;
        m_ulLastOsReadPacket = m_ulLastOsWritePacket = ULONG_MAX;
        m_HasLastWritePacket = FALSE;
        m_ulCurrentWritePosition = 0;
        m_IsCurrentWritePositionUpdated = 0;
    } else if (State_ == KSSTATE_RUN && previous != KSSTATE_RUN) {
        m_FrameClock.frequency = m_ullPerformanceCounterFrequency.QuadPart;
        m_FrameClock.rate = m_pWfExt->Format.nSamplesPerSec;
        m_FrameClock.anchor(now.QuadPart); // pause duration never consumes PCM
        m_ullDmaTimeStamp = deskfuse::mul_div(now.QuadPart, 10000000, m_FrameClock.frequency);
        ArmNotificationLocked();
    }
    if (State_!=previous) {
        const auto phaseHns=deskfuse::mul_div(m_FrameClock.remainder,10000000,m_ullPerformanceCounterFrequency.QuadPart) /
            m_pWfExt->Format.nSamplesPerSec;
        DeskFusePcmState(m_pMiniport->m_PcmSource,this,State_==KSSTATE_RUN,m_FrameClock.frames,
            deskfuse::mul_div(now.QuadPart,10000000,m_ullPerformanceCounterFrequency.QuadPart)-phaseHns);
    }
    if (State_ != KSSTATE_RUN) ExCancelTimer(m_pNotificationTimer, NULL);
    KeReleaseSpinLock(&m_PositionSpinLock, irql);
    return STATUS_SUCCESS;
}

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS CMiniportWaveRTStream::SetFormat
(
    _In_    KSDATAFORMAT    *DataFormat_
)
{
    UNREFERENCED_PARAMETER(DataFormat_);

    PAGED_CODE();

    return STATUS_NOT_SUPPORTED;
}

#pragma code_seg()

//=============================================================================
#pragma code_seg()
VOID CMiniportWaveRTStream::UpdatePosition(_In_ LARGE_INTEGER now)
{
    m_FrameClock.advance(now.QuadPart);
    const auto align = m_pWfExt->Format.nBlockAlign;
    m_ullPresentationPosition = m_FrameClock.frames * align;
    auto consumed = m_FrameClock.frames;
    if (m_bEoSReceived && consumed >= m_EosEndFrame) {
        consumed = m_EosEndFrame;
        m_bLastBufferRendered = TRUE;
    }
    const auto bytes = consumed * align;
    if (m_bCapture && bytes > m_ullLinearPosition && m_pDmaBuffer) {
        // A late callback cannot synthesize unbounded obsolete capture data.
        const auto delta = bytes - m_ullLinearPosition;
        if (delta > m_ulDmaBufferSize) m_ullLinearPosition = bytes - m_ulDmaBufferSize;
        WriteBytes(static_cast<ULONG>(bytes - m_ullLinearPosition));
    }
    m_ullLinearPosition = bytes;
    if (m_ulDmaBufferSize) m_ullPlayPosition = m_ullWritePosition = bytes % m_ulDmaBufferSize;
    if (m_PacketFrames) m_llPacketCounter = static_cast<LONGLONG>(consumed / m_PacketFrames +
        (m_bLastBufferRendered && consumed % m_PacketFrames != 0));
    m_ullDmaTimeStamp = deskfuse::mul_div(now.QuadPart, 10000000, m_FrameClock.frequency);
}

VOID CMiniportWaveRTStream::ArmNotificationLocked()
{
    if (m_ShuttingDown || m_KsState != KSSTATE_RUN || !m_PacketFrames || m_EosNotified) return;
    auto target = (m_FrameClock.frames / m_PacketFrames + 1) * m_PacketFrames;
    if (m_bEoSReceived && m_EosEndFrame < target) target = m_EosEndFrame;
    ExSetTimer(m_pNotificationTimer, -static_cast<LONGLONG>(m_FrameClock.due_hns(target)), 0, NULL);
}

//=============================================================================
#pragma code_seg()
VOID CMiniportWaveRTStream::WriteBytes
(
    _In_ ULONG ByteDisplacement
)
/*++

Routine Description:

This function writes the audio buffer using a sine wave generator
Arguments:

ByteDisplacement - # of bytes to process.

--*/
{
    ULONG bufferOffset = m_ullLinearPosition % m_ulDmaBufferSize;

    // Normally this will loop no more than once for a single wrap, but if
    // many bytes have been displaced then this may loops many times.
    while (ByteDisplacement > 0)
    {
        ULONG runWrite = min(ByteDisplacement, m_ulDmaBufferSize - bufferOffset);
            m_ToneGenerator.GenerateSine(m_pDmaBuffer + bufferOffset, runWrite);
        bufferOffset = (bufferOffset + runWrite) % m_ulDmaBufferSize;
        ByteDisplacement -= runWrite;
    }
}

//=============================================================================
#pragma code_seg("PAGE")
STDMETHODIMP_(NTSTATUS) 
CMiniportWaveRTStream::SetContentId
(
    _In_  ULONG                   contentId,
    _In_  PCDRMRIGHTS             drmRights
)
/*++

Routine Description:

  Sets DRM content Id for this stream. Also updates the Mixed content Id.

Arguments:

  contentId - new content id

  drmRights - rights for this stream.

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();

    DPF_ENTER(("[CMiniportWaveRT::SetContentId]"));

    if (!drmRights || drmRights->CopyProtect || drmRights->DigitalOutputDisable)
        DeskFusePcmDeny(m_pMiniport->m_PcmSource);

    NTSTATUS    ntStatus;
    ULONG       ulOldContentId = m_ulContentId;

    m_ulContentId = contentId;

    //
    // Miniport should create a mixed DrmRights.
    //
    ntStatus = m_pMiniport->UpdateDrmRights();

    //
    // Restore the passed-in content Id.
    //
    if (!NT_SUCCESS(ntStatus))
    {
        m_ulContentId = ulOldContentId;
        DeskFusePcmDeny(m_pMiniport->m_PcmSource);
    }

    //
    // From MSDN:
    //
    // This sample doesn't forward protected content, but if your driver uses 
    // lower layer drivers or a different stack to properly work, please see the 
    // following info from MSDN:
    //
    // "Before allowing protected content to flow through a data path, the system
    // verifies that the data path is secure. To do so, the system authenticates
    // each module in the data path beginning at the upstream end of the data path
    // and moving downstream. As each module is authenticated, that module gives
    // the system information about the next module in the data path so that it
    // can also be authenticated. To be successfully authenticated, a module's 
    // binary file must be signed as DRM-compliant.
    //
    // Two adjacent modules in the data path can communicate with each other in 
    // one of several ways. If the upstream module calls the downstream module 
    // through IoCallDriver, the downstream module is part of a WDM driver. In 
    // this case, the upstream module calls the DrmForwardContentToDeviceObject
    // function to provide the system with the device object representing the 
    // downstream module. (If the two modules communicate through the downstream
    // module's COM interface or content handlers, the upstream module calls 
    // DrmForwardContentToInterface or DrmAddContentHandlers instead.)
    //
    // DrmForwardContentToDeviceObject performs the same function as 
    // PcForwardContentToDeviceObject and IDrmPort2::ForwardContentToDeviceObject." 
    //
    // Other supported DRM DDIs for down-level module validation are: 
    // DrmForwardContentToInterfaces and DrmAddContentHandlers.
    //
    // For more information, see MSDN's DRM Functions and Interfaces.
    //

    return ntStatus;
} // SetContentId

#if defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)

//=============================================================================
#pragma code_seg()
NTSTATUS 
CMiniportWaveRTStream::GetSidebandStreamNtStatus()
/*++

Routine Description:

  Checks if the Sideband stream connection is up, if not, an error is returned.

Return Value:

  NT status code.

--*/
{
    DPF_ENTER(("[CMiniportWaveRTStream::GetSidebandStreamNtStatus]"));

    NTSTATUS  ntStatus  = STATUS_INVALID_DEVICE_STATE;
        
    if (m_SidebandStarted)
    {
        PSIDEBANDDEVICECOMMON sidebandDevice;
        
        ASSERT(m_pMiniport->IsSidebandDevice());
        sidebandDevice = m_pMiniport->GetSidebandDevice(); // weak ref.
        ASSERT(sidebandDevice != NULL);

        if (sidebandDevice->GetStreamStatus(m_pMiniport->m_DeviceType))
        {
            ntStatus = STATUS_SUCCESS;
        }
    }

    return ntStatus;        
}
#endif // defined(SYSVAD_BTH_BYPASS) || defined(SYSVAD_USB_SIDEBAND)

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS
CMiniportWaveRTStream::PropertyHandlerModulesListRequest
(
    _In_ PPCPROPERTY_REQUEST      PropertyRequest
)
{
    // This specific APO->driver communication example is mainly added to show 
    // how this communication is done. The instance module list lives on the 
    // stream object and it can only have modules associated with the underline
    // stream's pin.

    PAGED_CODE();

    DPF_ENTER(("[CMiniportWaveRTStream::PropertyHandlerModulesListRequest]"));

    return AudioModule_GenericHandler_ModulesListRequest(
                PropertyRequest,
                GetAudioModuleList(),
                GetAudioModuleListCount());
} // PropertyHandlerModulesListRequest

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS
CMiniportWaveRTStream::PropertyHandlerModuleCommand
(
    _In_ PPCPROPERTY_REQUEST      PropertyRequest
)
{
    PAGED_CODE();

    DPF_ENTER(("[CMiniportWaveRTStream::PropertyHandlerModuleCommand]"));

    return AudioModule_GenericHandler_ModuleCommand(
                PropertyRequest,
                GetAudioModuleList(),
                GetAudioModuleListCount());
} // PropertyHandlerModuleCommand

//=============================================================================
#pragma code_seg()
void TimerNotifyRT(_In_ PEX_TIMER Timer, _In_opt_ PVOID DeferredContext)
{
    UNREFERENCED_PARAMETER(Timer);
    auto stream = static_cast<CMiniportWaveRTStream*>(DeferredContext);
    if (!stream) return;
    KIRQL irql;
    KeAcquireSpinLock(&stream->m_PositionSpinLock, &irql);
    if (!stream->m_ShuttingDown && stream->m_KsState == KSSTATE_RUN) {
        const auto now = KeQueryPerformanceCounter(NULL);
        stream->UpdatePosition(now);
        const auto completed = static_cast<ULONGLONG>(stream->m_llPacketCounter);
        const bool terminal = stream->m_bLastBufferRendered && !stream->m_EosNotified;
        if (completed > stream->m_LastNotifiedPacket || terminal) {
            if (completed > stream->m_LastNotifiedPacket + 1)
                stream->m_MissedNotifications += completed - stream->m_LastNotifiedPacket - 1;
            stream->m_LastNotifiedPacket = completed;
            if (terminal) stream->m_EosNotified = TRUE;
            // Exactly one progress signal per wake, even after many missed periods.
            for (auto e = stream->m_NotificationList.Flink;
                 e != &stream->m_NotificationList; e = e->Flink) {
                auto n = CONTAINING_RECORD(e, NotificationListEntry, ListEntry);
                KeSetEvent(n->NotificationEvent, IO_NO_INCREMENT, FALSE);
            }
        }
        stream->ArmNotificationLocked();
    }
    KeReleaseSpinLock(&stream->m_PositionSpinLock, irql);
}
//=============================================================================


