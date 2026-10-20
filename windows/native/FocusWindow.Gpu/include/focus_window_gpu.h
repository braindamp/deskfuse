#pragma once
#include <stdint.h>
// Versioned in-process GPU pipeline; one owner, no file output or sidecar EXE.
#define FWGPU_EXPORT extern "C" __declspec(dllexport)
struct fwgpu_session;
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
