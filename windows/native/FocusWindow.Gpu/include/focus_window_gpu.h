#pragma once
#include <stdint.h>
// Versioned in-process GPU pipeline; one owner, no file output or sidecar EXE.
#ifdef FWGPU_BUILD_DLL
#define FWGPU_EXPORT extern "C" __declspec(dllexport)
#else
#define FWGPU_EXPORT extern "C" __declspec(dllimport)
#endif
struct fwgpu_session;
// Optional additive diagnostics, version 1. All durations are local CPU microseconds;
// source acquisition includes pacing/materialization, not completed GPU-copy time.
struct fwgpu_metrics {
    uint32_t size;
    uint32_t version;
    uint64_t frames;
    uint64_t capture_arrival_fallback_aus;
    uint64_t qpc_frequency;
    uint64_t source_wait_max_us;
    uint64_t encode_submit_max_us;
    uint64_t bitstream_lock_max_us;
    uint64_t inspect_copy_max_us;
};
static_assert(sizeof(fwgpu_metrics) == 64);
FWGPU_EXPORT int32_t __cdecl fwgpu_get_metrics(fwgpu_session* handle, fwgpu_metrics* output, uint32_t capacity);
FWGPU_EXPORT uint32_t __cdecl fwgpu_abi_version();
FWGPU_EXPORT fwgpu_session* __cdecl fwgpu_open(void* hwnd, uint32_t width, uint32_t height,
    uint32_t fps, uint32_t bitrate);
FWGPU_EXPORT int32_t __cdecl fwgpu_read(fwgpu_session* handle, uint32_t timeout_ms,
    uint8_t* output, uint32_t capacity, uint32_t* written,
    uint64_t* capture_qpc, uint8_t* keyframe);
// Returns 1=access unit, 0=no frame, -1=fatal, -2=insufficient buffer
FWGPU_EXPORT void __cdecl fwgpu_force_idr(fwgpu_session* handle);
FWGPU_EXPORT int32_t __cdecl fwgpu_last_error(fwgpu_session* handle, wchar_t* output, uint32_t capacity);
FWGPU_EXPORT void __cdecl fwgpu_close(fwgpu_session* handle);
// Open/read/close must run on the same owning MTA thread. Caller joins readers
// before close. A -2 read preserves the AU for a retry; no partial AU is copied.
// capture_qpc uses QueryPerformanceCounter units. Compositor time is preserved
// when available; capture-arrival fallback AUs are counted by optional metrics.
