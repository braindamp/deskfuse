#pragma once
#include <windows.h>
#include <cstdint>

// x64 ABI 1. Handles are borrowed until successful destroy. One consumer owns
// borrow/consume; lifecycle calls are serialized and never race a new borrow.
// No function frees the object on failure. Callers retain DLL custody as well.
constexpr uint32_t DF_LOOPBACK_ABI = 1;
enum : uint32_t {
    DF_LOOPBACK_READY = 1, DF_LOOPBACK_TERMINAL = 2, DF_LOOPBACK_FAILED = 4,
    DF_LOOPBACK_STOP_REQUESTED = 8, DF_LOOPBACK_BORROWED = 16,
    DF_LOOPBACK_JOINED = 32, DF_LOOPBACK_STOP_UNCONFIRMED = 64,
    DF_LOOPBACK_HAS_RECORDS = 128
};
struct DfLoopbackOptions {
    uint32_t size, abi;
    const wchar_t* endpoint; // copied by open; exact service-owned endpoint
    GUID source_instance;
    uint64_t driver_build;
    uint64_t setup_cutoff_100ns; // QueryInterruptTimePrecise; checked before Start/Ready
    uint32_t minimum_period, reserved;
};
struct DfLoopbackOpen {
    uint32_t size, abi;
    void* owner;
    HANDLE data, ready, terminal;
};
struct DfLoopbackRecord {
    uint32_t size, abi;
    uint64_t token, first_device_frame, qpc_100ns;
    uint32_t flags, frames, original_packet_offset_frames, pcm_bytes;
    const uint8_t* pcm;
};
struct DfLoopbackStatus {
    uint32_t size, abi, flags;
    int32_t error;
    uint64_t dropped;
    uint32_t period_frames, capacity_frames;
    GUID source_instance;
    uint64_t driver_build;
};
static_assert(sizeof(void*) == 8, "owned loopback requires x64");
static_assert(sizeof(DfLoopbackOptions) == 56 && sizeof(DfLoopbackOpen) == 40);
static_assert(sizeof(DfLoopbackRecord) == 56 && sizeof(DfLoopbackStatus) == 56);

extern "C" {
__declspec(dllexport) int deskfuse_open_owned_loopback(const DfLoopbackOptions*, DfLoopbackOpen*) noexcept;
// 1 record, 0 empty, -1 invalid/busy. Published PCM is immutable until consume.
__declspec(dllexport) int deskfuse_loopback_try_borrow(void*, DfLoopbackRecord*) noexcept;
__declspec(dllexport) int deskfuse_loopback_consume(void*, uint64_t) noexcept;
__declspec(dllexport) int deskfuse_loopback_request_stop(void*) noexcept;
// 0 confirmed, 1 timeout, -1 error/unconfirmed Stop. Object is always retained.
__declspec(dllexport) int deskfuse_loopback_join(void*, uint32_t timeout_ms) noexcept;
__declspec(dllexport) int deskfuse_loopback_destroy(void*) noexcept;
__declspec(dllexport) int deskfuse_loopback_status(void*, DfLoopbackStatus*) noexcept;
}
