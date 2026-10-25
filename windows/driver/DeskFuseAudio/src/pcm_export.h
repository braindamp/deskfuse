#pragma once
#include "pcm_export_abi.h"
struct DeskFusePcmSource;
struct DeskFusePcmReservation { void* session; ULONG slot; ULONGLONG generation; };
NTSTATUS DeskFusePcmInitialize(PDRIVER_OBJECT driver);
void DeskFusePcmShutdown();
NTSTATUS DeskFusePcmSourceCreate(const GUID& id, DeskFusePcmSource** source);
void DeskFusePcmSourceDestroy(DeskFusePcmSource* source);
void DeskFusePcmStreamOpen(DeskFusePcmSource* source, void* stream, bool eligible);
void DeskFusePcmStreamClose(DeskFusePcmSource* source, void* stream);
void DeskFusePcmState(DeskFusePcmSource* source, void* stream, bool running,
    ULONGLONG frame, ULONGLONG qpc100ns);
void DeskFusePcmDeny(DeskFusePcmSource* source);
bool DeskFusePcmReserve(DeskFusePcmSource* source, void* stream, ULONGLONG frame,
    ULONG frames, ULONGLONG commitQpc100ns, DeskFusePcmReservation* reservation);
void DeskFusePcmPublish(DeskFusePcmSource* source, const DeskFusePcmReservation& reservation,
    const UCHAR* pcm, ULONG frames);
