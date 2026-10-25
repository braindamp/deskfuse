#include <ntifs.h>
#include <wdmsec.h>
#include <ntstrsafe.h>
#include "pcm_export.h"
#include "pcm_credits.h"

namespace {
constexpr ULONG Tag='xPfD';
struct Session;
PDEVICE_OBJECT ControlDevice=nullptr;
PDRIVER_DISPATCH Previous[IRP_MJ_MAXIMUM_FUNCTION+1]={};
FAST_MUTEX RegistryLock;
LIST_ENTRY Sources;
bool ProcessNotifyRegistered=false;
constexpr GUID ControlClass={0x59f44001,0xae02,0x4503,{0x88,0x04,0xba,0x50,0xd6,0x21,0xfa,0x8a}};
struct Session {
    DeskFusePcmSource* source;
    PEPROCESS owner, consumer;
    HANDLE ownerHandle, consumerHandle, section, eventHandle;
    PKEVENT event;
    PVOID ownerView;
    PMDL mdl;
    DeskFusePcmMapping* map;
    EX_RUNDOWN_REF producers;
    deskfuse::PcmCredits credits;
    bool revoked, storageReleased;
};
struct FileContext { PEPROCESS creator; Session* session; ERESOURCE serial; };
struct FileSerial {
    ERESOURCE* lock;
    explicit FileSerial(ERESOURCE* value):lock(value) {KeEnterCriticalRegion();ExAcquireResourceExclusiveLite(lock,TRUE);}
    ~FileSerial() {ExReleaseResourceLite(lock);KeLeaveCriticalRegion();}
};
}
struct DeskFusePcmSource {
    LIST_ENTRY link;
    LONG references;
    GUID id;
    KSPIN_LOCK lock;
    Session* active;
    ULONG streams;
    void* stream;
    bool removed, running, denied;
    ULONGLONG generation, correlationFrame, correlationQpc;
    deskfuse::PcmGeometry geometry;
};
namespace {
void ReleaseSource(DeskFusePcmSource* source) {
    if (InterlockedDecrement(&source->references)==0) ExFreePoolWithTag(source,Tag);
}
void Signal(Session* s) { KeMemoryBarrier(); KeSetEvent(s->event,IO_NO_INCREMENT,FALSE); }
void RevokeLocked(DeskFusePcmSource* source) {
    auto s=source->active;
    if (!s || s->revoked) return;
    s->revoked=true;
    s->map->control.state=DESKFUSE_PCM_REVOKED;
    ++s->map->control.control_sequence;
    Signal(s);
    source->active=nullptr; // old storage will NEVER be attached to a new grant
}
void ProcessNotify(PEPROCESS process,HANDLE processId,PPS_CREATE_NOTIFY_INFO createInfo) {
    UNREFERENCED_PARAMETER(processId);
    if (createInfo) return;
    // Exit notification precedes address-space teardown. Publication stops
    // independently of whether the broker or Rust owner gets CPU time again.
    ExAcquireFastMutex(&RegistryLock);
    for (auto e=Sources.Flink;e!=&Sources;e=e->Flink) {
        auto source=CONTAINING_RECORD(e,DeskFusePcmSource,link);
        KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
        auto s=source->active;
        if (s && (s->owner==process || s->consumer==process)) RevokeLocked(source);
        KeReleaseSpinLock(&source->lock,irql);
    }
    ExReleaseFastMutex(&RegistryLock);
}
// View lifetime belongs to the section object, not a raw pool allocation.
// Rust's MAP_READ view retains the section after kernel rundown/unmapping.
// An independently terminated process therefore cannot leave dangling pages.
void ReleaseStorage(Session* s) {
    PAGED_CODE();
    if (s->storageReleased) return;
    auto source=s->source;
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    if (source->active==s) RevokeLocked(source);
    KeReleaseSpinLock(&source->lock,irql);
    ExWaitForRundownProtectionRelease(&s->producers);
    if (s->mdl) {
        if (s->map) MmUnmapLockedPages(s->map,s->mdl);
        MmUnlockPages(s->mdl);IoFreeMdl(s->mdl);s->mdl=nullptr;s->map=nullptr;
    }
    if (s->ownerView && s->ownerHandle) {
        // Zw operates on the referenced original process handle. If the broker
        // exited, MM already removed this view; never attach to a reused PID.
        ZwUnmapViewOfSection(s->ownerHandle,s->ownerView);s->ownerView=nullptr;
    }
    if (s->section) {ZwClose(s->section);s->section=nullptr;}
    s->storageReleased=true;
}
void DeleteSession(Session* s) {
    if (!s) return;
    ReleaseStorage(s);
    if (s->event) ObDereferenceObject(s->event);
    if (s->eventHandle) ZwClose(s->eventHandle);
    if (s->consumerHandle) ZwClose(s->consumerHandle);
    if (s->ownerHandle) ZwClose(s->ownerHandle);
    if (s->consumer) ObDereferenceObject(s->consumer);
    if (s->owner) ObDereferenceObject(s->owner);
    ReleaseSource(s->source);ExFreePoolWithTag(s,Tag);
}
NTSTATUS OpenProcessObject(PEPROCESS process,ACCESS_MASK access,HANDLE* handle) {
    return ObOpenObjectByPointer(process,OBJ_KERNEL_HANDLE,nullptr,access,*PsProcessType,KernelMode,handle);
}
NTSTATUS CreateStorage(Session* s) {
    OBJECT_ATTRIBUTES oa;InitializeObjectAttributes(&oa,nullptr,OBJ_KERNEL_HANDLE,nullptr,nullptr);
    LARGE_INTEGER size;size.QuadPart=(sizeof(DeskFusePcmMapping)+PAGE_SIZE-1)&~(PAGE_SIZE-1);
    auto status=ZwCreateSection(&s->section,SECTION_ALL_ACCESS,&oa,&size,PAGE_READWRITE,SEC_COMMIT,nullptr);
    if (!NT_SUCCESS(status)) return status;
    SIZE_T viewSize=0;
    status=ZwMapViewOfSection(s->section,ZwCurrentProcess(),&s->ownerView,0,0,nullptr,&viewSize,ViewUnmap,0,PAGE_READWRITE);
    if (!NT_SUCCESS(status)) return status;
    s->mdl=IoAllocateMdl(s->ownerView,static_cast<ULONG>(viewSize),FALSE,FALSE,nullptr);
    if (!s->mdl) return STATUS_INSUFFICIENT_RESOURCES;
    __try { MmProbeAndLockPages(s->mdl,KernelMode,IoWriteAccess); }
    __except(EXCEPTION_EXECUTE_HANDLER) {IoFreeMdl(s->mdl);s->mdl=nullptr;return GetExceptionCode();}
    s->map=static_cast<DeskFusePcmMapping*>(MmMapLockedPagesSpecifyCache(s->mdl,KernelMode,MmCached,nullptr,FALSE,NormalPagePriority|MdlMappingNoExecute));
    if (!s->map) return STATUS_INSUFFICIENT_RESOURCES;
    RtlZeroMemory(s->map,sizeof(*s->map));
    status=ZwCreateEvent(&s->eventHandle,EVENT_ALL_ACCESS,&oa,NotificationEvent,FALSE);
    if (!NT_SUCCESS(status)) return status;
    return ObReferenceObjectByHandle(s->eventHandle,EVENT_ALL_ACCESS,*ExEventObjectType,KernelMode,
        reinterpret_cast<PVOID*>(&s->event),nullptr);
}
NTSTATUS Bind(FileContext* file,const DeskFusePcmBind& request,DeskFusePcmHandles* result) {
    if (file->session || request.size!=sizeof(request) || request.abi!=DESKFUSE_PCM_ABI ||
        PsGetCurrentProcess()!=file->creator || !request.process_handle) return STATUS_ACCESS_DENIED;
    DeskFusePcmSource* source=nullptr;
    ExAcquireFastMutex(&RegistryLock);
    for (auto e=Sources.Flink;e!=&Sources;e=e->Flink) {
        auto candidate=CONTAINING_RECORD(e,DeskFusePcmSource,link);
        if (RtlEqualMemory(&candidate->id,&request.source,sizeof(GUID))) {
            source=candidate;InterlockedIncrement(&source->references);break;
        }
    }
    ExReleaseFastMutex(&RegistryLock);
    if (!source) return STATUS_NOT_FOUND;
    auto s=static_cast<Session*>(ExAllocatePool2(POOL_FLAG_NON_PAGED,sizeof(Session),Tag));
    if (!s) {ReleaseSource(source);return STATUS_INSUFFICIENT_RESOURCES;}
    s->source=source;ExInitializeRundownProtection(&s->producers);
    s->owner=file->creator;ObReferenceObject(s->owner);
    auto status=ObReferenceObjectByHandle(reinterpret_cast<HANDLE>(request.process_handle),PROCESS_DUP_HANDLE,
        *PsProcessType,UserMode,reinterpret_cast<PVOID*>(&s->consumer),nullptr);
    if (NT_SUCCESS(status)) status=OpenProcessObject(s->owner,0x0008 /* PROCESS_VM_OPERATION */,&s->ownerHandle);
    if (NT_SUCCESS(status)) status=OpenProcessObject(s->consumer,PROCESS_DUP_HANDLE,&s->consumerHandle);
    if (NT_SUCCESS(status)) status=CreateStorage(s);
    HANDLE remoteSection=nullptr,remoteEvent=nullptr;
    if (NT_SUCCESS(status)) status=ZwDuplicateObject(ZwCurrentProcess(),s->section,s->consumerHandle,
        &remoteSection,SECTION_MAP_READ|SECTION_QUERY,0,0);
    if (NT_SUCCESS(status)) status=ZwDuplicateObject(ZwCurrentProcess(),s->eventHandle,s->consumerHandle,
        &remoteEvent,SYNCHRONIZE,0,0);
    if (NT_SUCCESS(status)) {
        KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
        if (source->active || source->removed || source->denied || source->streams>1) status=STATUS_DEVICE_BUSY;
        else {
            auto& c=s->map->control;c.size=sizeof(c);c.abi=DESKFUSE_PCM_ABI;c.source=source->id;
            c.generation=source->generation;c.control_sequence=1;c.geometry=source->geometry;
            c.state=source->running?DESKFUSE_PCM_RUNNING:DESKFUSE_PCM_WAITING;
            c.correlation_frame=source->correlationFrame;c.correlation_qpc_100ns=source->correlationQpc;
            source->active=s;Signal(s);file->session=s;
        }
        KeReleaseSpinLock(&source->lock,irql);
    }
    if (!NT_SUCCESS(status)) {
        // Duplicate-close in the referenced target process on partial setup.
        HANDLE ignored=nullptr;
        if (remoteSection) ZwDuplicateObject(s->consumerHandle,remoteSection,nullptr,&ignored,0,0,DUPLICATE_CLOSE_SOURCE);
        if (remoteEvent) ZwDuplicateObject(s->consumerHandle,remoteEvent,nullptr,&ignored,0,0,DUPLICATE_CLOSE_SOURCE);
        DeleteSession(s);return status;
    }
    *result={sizeof(*result),DESKFUSE_PCM_ABI,reinterpret_cast<ULONGLONG>(remoteSection),
        reinterpret_cast<ULONGLONG>(remoteEvent),s->map->control.generation};
    return STATUS_SUCCESS;
}
NTSTATUS Dispatch(PDEVICE_OBJECT device,PIRP irp) {
    auto stack=IoGetCurrentIrpStackLocation(irp);
    if (device!=ControlDevice) return Previous[stack->MajorFunction](device,irp);
    NTSTATUS status=STATUS_INVALID_DEVICE_REQUEST;ULONG_PTR information=0;
    auto file=static_cast<FileContext*>(stack->FileObject->FsContext);
    if (KeGetCurrentIrql()!=PASSIVE_LEVEL) {status=STATUS_INVALID_DEVICE_STATE;goto Done;}
    switch (stack->MajorFunction) {
    case IRP_MJ_CREATE:
        file=static_cast<FileContext*>(ExAllocatePool2(POOL_FLAG_NON_PAGED,sizeof(FileContext),Tag));
        if (!file) {status=STATUS_INSUFFICIENT_RESOURCES;break;}
        status=ExInitializeResourceLite(&file->serial);
        if (!NT_SUCCESS(status)) {ExFreePoolWithTag(file,Tag);break;}
        file->creator=PsGetCurrentProcess();ObReferenceObject(file->creator);
        stack->FileObject->FsContext=file;status=STATUS_SUCCESS;break;
    case IRP_MJ_CLEANUP:
        if (file) {FileSerial serial(&file->serial);if (file->session) ReleaseStorage(file->session);}
        status=STATUS_SUCCESS;break;
    case IRP_MJ_CLOSE:
        if (file) {DeleteSession(file->session);ExDeleteResourceLite(&file->serial);ObDereferenceObject(file->creator);ExFreePoolWithTag(file,Tag);stack->FileObject->FsContext=nullptr;}
        status=STATUS_SUCCESS;break;
    case IRP_MJ_DEVICE_CONTROL: {
        if (!file) {status=STATUS_INVALID_HANDLE;break;}
        FileSerial serial(&file->serial);
        auto buffer=irp->AssociatedIrp.SystemBuffer;
        const auto input=stack->Parameters.DeviceIoControl.InputBufferLength;
        const auto output=stack->Parameters.DeviceIoControl.OutputBufferLength;
        const auto code=stack->Parameters.DeviceIoControl.IoControlCode;
        if (code==DESKFUSE_PCM_BIND) {
            if (input!=sizeof(DeskFusePcmBind) || output<sizeof(DeskFusePcmHandles)) {status=STATUS_BUFFER_TOO_SMALL;break;}
            const auto request=*static_cast<DeskFusePcmBind*>(buffer);
            status=Bind(file,request,static_cast<DeskFusePcmHandles*>(buffer));
            if (NT_SUCCESS(status)) information=sizeof(DeskFusePcmHandles);
            break;
        }
        auto s=file->session;
        if (!s || (PsGetCurrentProcess()!=s->consumer && PsGetCurrentProcess()!=s->owner)) {status=STATUS_ACCESS_DENIED;break;}
        if (code==DESKFUSE_PCM_REVOKE) {ReleaseStorage(s);status=STATUS_SUCCESS;break;}
        auto source=s->source;
        KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
        if (code==DESKFUSE_PCM_SNAPSHOT && s->map && output>=sizeof(DeskFusePcmControl)) {
            *static_cast<DeskFusePcmControl*>(buffer)=s->map->control;
            information=sizeof(DeskFusePcmControl);status=STATUS_SUCCESS;
        } else if (source->active!=s || s->revoked || !s->map) status=STATUS_DEVICE_REMOVED;
        else if (code==DESKFUSE_PCM_FENCE && output>=sizeof(DeskFusePcmControl)) {
            // FENCE and accepted/in-flight reservation assignment use this same lock.
            *static_cast<DeskFusePcmControl*>(buffer)=s->map->control;
            information=sizeof(DeskFusePcmControl);status=STATUS_SUCCESS;
        } else if (code==DESKFUSE_PCM_ACK_ARM && input==sizeof(DeskFusePcmAck)) {
            const auto ack=*static_cast<DeskFusePcmAck*>(buffer);
            if (ack.generation!=s->map->control.generation || ack.ticket<s->credits.acknowledged ||
                ack.ticket>s->credits.nextTicket || ack.observed_control>s->map->control.control_sequence) status=STATUS_INVALID_PARAMETER;
            else {
                status=STATUS_SUCCESS;
                if (!s->credits.ack(ack.ticket)) status=STATUS_INVALID_PARAMETER;
                if (NT_SUCCESS(status)) {
                    s->map->control.acknowledged=s->credits.acknowledged;
                    // Atomic empty check + reset under the publication lock.
                    if (s->credits.may_arm(ack.observed_control,s->map->control.control_sequence,
                        s->map->control.state==DESKFUSE_PCM_WAITING))
                        KeClearEvent(s->event);
                    else Signal(s);
                }
            }
        } else status=STATUS_INVALID_PARAMETER;
        KeReleaseSpinLock(&source->lock,irql);
        break;
    }
    }
Done:
    irp->IoStatus.Status=status;irp->IoStatus.Information=information;IoCompleteRequest(irp,IO_NO_INCREMENT);return status;
}
}

NTSTATUS DeskFusePcmInitialize(PDRIVER_OBJECT driver) {
    ExInitializeFastMutex(&RegistryLock);InitializeListHead(&Sources);
    UNICODE_STRING name=RTL_CONSTANT_STRING(L"\\Device\\DeskFusePcm");
    UNICODE_STRING link=RTL_CONSTANT_STRING(L"\\DosDevices\\DeskFusePcm");
    UNICODE_STRING acl=RTL_CONSTANT_STRING(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    auto status=IoCreateDeviceSecure(driver,0,&name,FILE_DEVICE_UNKNOWN,FILE_DEVICE_SECURE_OPEN,FALSE,&acl,&ControlClass,&ControlDevice);
    if (!NT_SUCCESS(status)) return status;
    status=IoCreateSymbolicLink(&link,&name);
    if (!NT_SUCCESS(status)) {IoDeleteDevice(ControlDevice);ControlDevice=nullptr;return status;}
    status=PsSetCreateProcessNotifyRoutineEx(ProcessNotify,FALSE);
    if (!NT_SUCCESS(status)) {IoDeleteSymbolicLink(&link);IoDeleteDevice(ControlDevice);ControlDevice=nullptr;return status;}
    ProcessNotifyRegistered=true;
    const UCHAR majors[]={IRP_MJ_CREATE,IRP_MJ_CLEANUP,IRP_MJ_CLOSE,IRP_MJ_DEVICE_CONTROL};
    for (auto major : majors) {
        Previous[major]=driver->MajorFunction[major];driver->MajorFunction[major]=Dispatch;
    }
    ControlDevice->Flags&=~DO_DEVICE_INITIALIZING;return STATUS_SUCCESS;
}
void DeskFusePcmShutdown() {
    if (ProcessNotifyRegistered) {PsSetCreateProcessNotifyRoutineEx(ProcessNotify,TRUE);ProcessNotifyRegistered=false;}
    if (!ControlDevice) return;
    UNICODE_STRING link=RTL_CONSTANT_STRING(L"\\DosDevices\\DeskFusePcm");
    IoDeleteSymbolicLink(&link);IoDeleteDevice(ControlDevice);ControlDevice=nullptr;
}
NTSTATUS DeskFusePcmSourceCreate(const GUID& id,DeskFusePcmSource** result) {
    auto source=static_cast<DeskFusePcmSource*>(ExAllocatePool2(POOL_FLAG_NON_PAGED,sizeof(DeskFusePcmSource),Tag));
    if (!source) return STATUS_INSUFFICIENT_RESOURCES;
    source->id=id;source->references=1;source->generation=1;KeInitializeSpinLock(&source->lock);
    ExAcquireFastMutex(&RegistryLock);InsertTailList(&Sources,&source->link);ExReleaseFastMutex(&RegistryLock);
    *result=source;return STATUS_SUCCESS;
}
void DeskFusePcmSourceRetire(DeskFusePcmSource* source) {
    if (!source) return;
    // Registry -> source is the same lock order as process exit. Idempotent:
    // retirement detaches identity/publication, destruction releases ownership.
    ExAcquireFastMutex(&RegistryLock);
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    if (!source->removed) {
        source->removed=true;RemoveEntryList(&source->link);RevokeLocked(source);
    }
    KeReleaseSpinLock(&source->lock,irql);ExReleaseFastMutex(&RegistryLock);
}
void DeskFusePcmSourceDestroy(DeskFusePcmSource* source) {
    if (!source) return;
    DeskFusePcmSourceRetire(source);ReleaseSource(source);
}
NTSTATUS DeskFusePcmSourceQuery(const GUID& id,DeskFuseTimingCapabilities* result) {
    if (!result) return STATUS_INVALID_PARAMETER;
    DeskFusePcmSource* source=nullptr;
    ExAcquireFastMutex(&RegistryLock);
    for (auto e=Sources.Flink;e!=&Sources;e=e->Flink) {
        auto candidate=CONTAINING_RECORD(e,DeskFusePcmSource,link);
        if (RtlEqualMemory(&candidate->id,&id,sizeof(id))) {
            source=candidate;InterlockedIncrement(&source->references);break;
        }
    }
    ExReleaseFastMutex(&RegistryLock);
    if (!source) return STATUS_DEVICE_REMOVED;
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    const auto status=source->removed?STATUS_DEVICE_REMOVED:STATUS_SUCCESS;
    if (NT_SUCCESS(status)) *result={sizeof(*result),DESKFUSE_TIMING_ABI,
        DESKFUSE_TIMING_BUILD,source->id,DESKFUSE_TIMING_REQUIRED,DESKFUSE_PCM_ABI};
    KeReleaseSpinLock(&source->lock,irql);ReleaseSource(source);return status;
}
void DeskFusePcmStreamOpen(DeskFusePcmSource* source,void* stream,bool eligible) {
    if (!source) return;
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    ++source->streams;
    if (source->streams==1 && eligible && !source->removed) {source->stream=stream;source->denied=false;source->geometry={};}
    else {source->stream=nullptr;source->denied=true;RevokeLocked(source);}
    KeReleaseSpinLock(&source->lock,irql);
}
void DeskFusePcmStreamClose(DeskFusePcmSource* source,void* stream) {
    UNREFERENCED_PARAMETER(stream);if (!source) return;
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    RevokeLocked(source);++source->generation;source->running=false;source->stream=nullptr;
    if (source->streams) --source->streams;
    if (!source->streams) {source->denied=false;source->geometry={};}
    KeReleaseSpinLock(&source->lock,irql);
}
void DeskFusePcmState(DeskFusePcmSource* source,void* stream,bool running,ULONGLONG frame,ULONGLONG qpc) {
    if (!source) return;
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    if (source->stream==stream) {
        if (!running && source->running) {RevokeLocked(source);++source->generation;}
        source->running=running;source->correlationFrame=frame;source->correlationQpc=running?qpc:0;
        if (source->active) {
            auto& c=source->active->map->control;c.state=running?DESKFUSE_PCM_RUNNING:DESKFUSE_PCM_WAITING;
            c.correlation_frame=frame;c.correlation_qpc_100ns=running?qpc:0;++c.control_sequence;Signal(source->active);
        }
    }
    KeReleaseSpinLock(&source->lock,irql);
}
void DeskFusePcmGeometryUpdate(DeskFusePcmSource* source,void* stream,
    ULONG notifications,ULONG packetFrames,ULONG rate,ULONG channels,ULONG bits) {
    if (!source) return;
    const auto next=deskfuse::pcm_geometry(notifications,packetFrames,rate,channels,bits);
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    if (source->stream==stream && !source->removed) {
        const bool changed=source->geometry.state==1 && !deskfuse::same_geometry(source->geometry,next);
        source->geometry=next;
        if (source->active) {
            auto s=source->active;s->map->control.geometry=next;++s->map->control.control_sequence;
            if (changed) RevokeLocked(source);else Signal(s);
        }
    }
    KeReleaseSpinLock(&source->lock,irql);
}
void DeskFusePcmDeny(DeskFusePcmSource* source) {
    if (!source) return;
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);source->denied=true;RevokeLocked(source);KeReleaseSpinLock(&source->lock,irql);
}
bool DeskFusePcmReserve(DeskFusePcmSource* source,void* stream,ULONGLONG frame,ULONG frames,ULONGLONG qpc,DeskFusePcmReservation* reservation) {
    if (!source) return false;
    bool reserved=false;KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    auto s=source->active;
    if (s && !source->denied && source->stream==stream && !s->revoked) {
        auto& c=s->map->control;++c.accepted_commit;c.accepted_end_frame=frame+frames;
        if (source->geometry.state==1 && frames<=source->geometry.packetFrames &&
            s->credits.can_reserve(frames) && ExAcquireRundownProtection(&s->producers)) {
            const auto ticket=s->credits.reserve(frames);const auto index=static_cast<ULONG>((ticket-1)%DESKFUSE_PCM_SLOTS);
            auto& slot=s->map->slots[index];slot.ticket=0;slot.generation=c.generation;
            slot.commit_sequence=c.accepted_commit;slot.commit_qpc_100ns=qpc;slot.first_frame=frame;slot.frames=frames;
            *reservation={s,index,c.generation};reserved=true;
        } else {c.dropped_frames+=frames;++c.control_sequence;Signal(s);}
    }
    KeReleaseSpinLock(&source->lock,irql);return reserved;
}
void DeskFusePcmPublish(DeskFusePcmSource* source,const DeskFusePcmReservation& r,const UCHAR* pcm,ULONG frames) {
    auto s=static_cast<Session*>(r.session);
    // Synchronous call from SetWritePacket, before PortCls can reuse its valid
    // accepted cyclic packet. No worker, wait, allocation, or network operation.
    RtlCopyMemory(s->map->slots[r.slot].pcm,pcm,frames*DESKFUSE_PCM_BYTES_PER_FRAME);
    KIRQL irql;KeAcquireSpinLock(&source->lock,&irql);
    if (source->active==s && !s->revoked && r.generation==s->map->control.generation) {
        auto& meta=s->credits.slots[r.slot];s->credits.publish(meta.ticket);KeMemoryBarrier();
        s->map->slots[r.slot].ticket=meta.ticket;s->map->control.published=meta.ticket;Signal(s);
    }
    KeReleaseSpinLock(&source->lock,irql);ExReleaseRundownProtection(&s->producers);
}
