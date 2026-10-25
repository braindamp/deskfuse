#pragma once
#include "timing_abi.h"
#include "pcm_geometry.h"
#define DESKFUSE_PCM_ABI 2u
#define DESKFUSE_PCM_SLOTS 8u
#define DESKFUSE_PCM_FRAMES 960u
#define DESKFUSE_PCM_BYTES_PER_FRAME 4u
#define DESKFUSE_PCM_WAITING 0u
#define DESKFUSE_PCM_RUNNING 1u
#define DESKFUSE_PCM_REVOKED 2u
#define DESKFUSE_PCM_IOCTL(n) CTL_CODE(FILE_DEVICE_UNKNOWN, (0x800u+(n)), METHOD_BUFFERED, FILE_READ_DATA|FILE_WRITE_DATA)
#define DESKFUSE_PCM_BIND DESKFUSE_PCM_IOCTL(0)
#define DESKFUSE_PCM_SNAPSHOT DESKFUSE_PCM_IOCTL(1)
#define DESKFUSE_PCM_ACK_ARM DESKFUSE_PCM_IOCTL(2)
#define DESKFUSE_PCM_FENCE DESKFUSE_PCM_IOCTL(3)
#define DESKFUSE_PCM_REVOKE DESKFUSE_PCM_IOCTL(4)
struct DeskFusePcmBind { unsigned int size, abi; GUID source; unsigned long long process_handle; };
struct DeskFusePcmHandles { unsigned int size, abi; unsigned long long section, event, generation; };
struct DeskFusePcmControl {
    unsigned int size, abi, state, reserved;
    GUID source;
    unsigned long long generation, control_sequence, accepted_commit, accepted_end_frame, dropped_frames;
    unsigned long long correlation_frame, correlation_qpc_100ns, published, acknowledged;
    deskfuse::PcmGeometry geometry; // 0 waiting, 1 qualified, 2 unsupported
};
struct DeskFusePcmSlot {
    unsigned long long ticket, generation, commit_sequence, commit_qpc_100ns, first_frame;
    unsigned int frames, reserved;
    unsigned char pcm[DESKFUSE_PCM_FRAMES*DESKFUSE_PCM_BYTES_PER_FRAME];
};
struct DeskFusePcmMapping { DeskFusePcmControl control; DeskFusePcmSlot slots[DESKFUSE_PCM_SLOTS]; };
struct DeskFusePcmAck { unsigned long long generation, ticket, observed_control; };
static_assert(sizeof(DeskFusePcmSlot)==3888, "PCM ABI packing");
static_assert(sizeof(DeskFusePcmControl)==128, "control ABI packing");
