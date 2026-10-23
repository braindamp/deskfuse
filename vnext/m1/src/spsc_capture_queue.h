#pragma once
// One WASAPI capture producer, one blocking pipe writer. Producer never owns
// a mutex, waits for space, allocates, or performs file I/O.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <stdexcept>
#include <type_traits>

namespace df_wasapi {

template <typename Block, std::size_t Slots>
class SpscCaptureQueue {
    static_assert(Slots > 0);
    static_assert(std::is_nothrow_copy_assignable_v<Block>);
public:
    SpscCaptureQueue() : wake_(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {
        if (!wake_) throw std::runtime_error("CreateEvent failed for WASAPI writer");
    }
    ~SpscCaptureQueue() { CloseHandle(wake_); }
    SpscCaptureQueue(const SpscCaptureQueue&) = delete;
    SpscCaptureQueue& operator=(const SpscCaptureQueue&) = delete;

    // Called only by the event-driven WASAPI capture thread. This copy is
    // bounded by one preallocated block. Full means drop, never wait.
    bool try_push(const Block& block) noexcept {
        if (closed_.load(std::memory_order_acquire)) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        const uint64_t wr = write_.load(std::memory_order_relaxed);
        const uint64_t rd = read_.load(std::memory_order_acquire);
        if (wr - rd >= Slots) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        slots_[wr % Slots] = block;
        write_.store(wr + 1, std::memory_order_release);
        // The event is a wake hint, not the queue's correctness mechanism.
        // The kernel keeps it signaled if the writer has not started waiting.
        SetEvent(wake_);
        return true;
    }

    // Writer-only. Drain all accepted blocks before observing close.
    // No capture-side ownership or lock is ever required.
    bool pop(Block& block) {
        for (;;) {
            const uint64_t rd = read_.load(std::memory_order_relaxed);
            const uint64_t wr = write_.load(std::memory_order_acquire);
            if (rd != wr) {
                block = slots_[rd % Slots];
                read_.store(rd + 1, std::memory_order_release);
                return true;
            }
#ifdef DESKFUSE_QUEUE_TEST_PROBE
            // Deterministic host-only pause after the empty snapshot.
            if (empty_probe_) empty_probe_(empty_probe_arg_);
#endif
            if (closed_.load(std::memory_order_acquire)) {
                // Producer publishes its final write_ BEFORE release-closing.
                // Acquire-close synchronizes with that publication. Re-read the
                // producer cursor: the first empty snapshot might predate the
                // final accepted block. Return only when the final cursor is
                // empty; otherwise loop and drain the committed PCM.
                if (write_.load(std::memory_order_acquire) == rd) return false;
                continue;
            }
            if (WaitForSingleObject(wake_, INFINITE) != WAIT_OBJECT_0) {
                throw std::runtime_error("WASAPI writer event wait failed");
            }
        }
    }

    // Capture producer owns termination. Closing does not discard accepted
    // PCM; the writer drains first and then returns false from pop().
    void finish() noexcept {
        closed_.store(true, std::memory_order_release);
        SetEvent(wake_);
    }

#ifdef DESKFUSE_QUEUE_TEST_PROBE
    using EmptyProbe = void (*)(void*);
    // Install only before starting the producer/consumer threads. Not
    // present in production WASAPI builds.
    void set_empty_probe(EmptyProbe callback, void* context) noexcept {
        empty_probe_ = callback;
        empty_probe_arg_ = context;
    }
#endif

    uint64_t dropped() const noexcept {
        return dropped_.load(std::memory_order_relaxed);
    }
private:
    std::array<Block, Slots> slots_{};
    std::atomic<uint64_t> read_{0}, write_{0}, dropped_{0};
    std::atomic<bool> closed_{false};
    HANDLE wake_;
#ifdef DESKFUSE_QUEUE_TEST_PROBE
    EmptyProbe empty_probe_ = nullptr;
    void* empty_probe_arg_ = nullptr;
#endif
};
} // namespace df_wasapi
