#pragma once
// Fixed x64 ABI. Queried on the exact endpoint's KS filter, never by filename.
// Bump build for any clock/ownership semantic change, ABI for layout changes.
#define DESKFUSE_TIMING_ABI 1u
#define DESKFUSE_TIMING_BUILD 0x2026102501ull
#define DESKFUSE_TIMING_PROPERTY 0u
#define DESKFUSE_TIMING_FRAME_CLOCK 1u
#define DESKFUSE_TIMING_ONESHOT 2u
#define DESKFUSE_TIMING_REQUIRED 3u
static const GUID KSPROPSETID_DeskFuseTiming =
    {0xe54c0a8b,0x725f,0x458b,{0x89,0x5a,0xae,0x13,0x19,0xc7,0xfb,0x41}};
struct DeskFuseTimingCapabilities {
    unsigned int size;
    unsigned int abi;
    unsigned long long build;
    GUID source_instance;
    unsigned int flags;
    unsigned int export_abi; // zero until a complete authorized export exists
};
inline bool DeskFuseTimingCompatible(const DeskFuseTimingCapabilities& c) {
    const GUID zero = {};
    return c.size == sizeof(c) && c.abi == DESKFUSE_TIMING_ABI &&
        c.build == DESKFUSE_TIMING_BUILD && (c.flags & DESKFUSE_TIMING_REQUIRED) == DESKFUSE_TIMING_REQUIRED &&
        !IsEqualGUID(c.source_instance, zero);
}
