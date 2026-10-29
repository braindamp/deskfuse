#pragma once
// One WASAPI capture producer, one consumer (native borrow or lab pipe writer). Producer never owns
// a mutex, waits for space, allocates, or performs file I/O.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
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
        Block* reserved = nullptr; uint64_t token = 0;
        if (!try_reserve(reserved, token)) return false;
        *reserved = block;
        return publish(token);
    }

    // Producer-only. A full queue is detected BEFORE PCM conversion/copy.
    // A reserved slot is private to the producer until release-publication.
    bool try_reserve(Block*& block, uint64_t& token) noexcept {
        block = nullptr; token = 0;
        if (reserved_) return false;
        if (closed_.load(std::memory_order_acquire)) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        const uint64_t wr = write_.load(std::memory_order_relaxed);
        if (wr == UINT64_MAX) { finish(); return false; }
        const uint64_t rd = read_.load(std::memory_order_acquire);
        if (wr - rd >= Slots) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        reserved_ = true; block = &slots_[wr % Slots]; token = wr + 1;
        return true;
    }
    bool publish(uint64_t token) noexcept {
        const uint64_t wr = write_.load(std::memory_order_relaxed);
        if (!reserved_ || token == 0 || token != wr + 1) return false;
        reserved_ = false;
        write_.store(wr + 1, std::memory_order_release);
        // The event is a wake hint, not the queue's correctness mechanism.
        // The kernel keeps it signaled if the writer has not started waiting.
        SetEvent(wake_);
        return true;
    }

    // Consumer-only. Exactly one immutable slot borrow may be outstanding.
    bool try_borrow(const Block*& block, uint64_t& token) noexcept {
        block = nullptr; token = 0;
        if (borrowed_.load(std::memory_order_relaxed)) return false;
        const uint64_t rd = read_.load(std::memory_order_relaxed);
        if (rd == write_.load(std::memory_order_acquire)) return false;
        token = rd + 1; block = &slots_[rd % Slots];
        borrowed_.store(true, std::memory_order_release); return true;
    }
    bool consume(uint64_t token) noexcept {
        const uint64_t rd = read_.load(std::memory_order_relaxed);
        if (!borrowed_.load(std::memory_order_acquire) || token == 0 || token != rd + 1) return false;
        read_.store(rd + 1, std::memory_order_release);
        borrowed_.store(false, std::memory_order_release); return true;
    }
    bool has_borrow() const noexcept { return borrowed_.load(std::memory_order_acquire); }
    bool closed() const noexcept { return closed_.load(std::memory_order_acquire); }
    bool has_records() const noexcept { return read_.load(std::memory_order_acquire) != write_.load(std::memory_order_acquire); }
    HANDLE event() const noexcept { return wake_; }

    // Writer-only. Drain all accepted blocks before observing close.
    // No capture-side ownership or lock is ever required.
    bool pop(Block& block) {
        for (;;) {
            const uint64_t rd = read_.load(std::memory_order_relaxed);
            const Block* borrowed = nullptr; uint64_t token = 0;
            if (try_borrow(borrowed, token)) {
                block = *borrowed;
                if (!consume(token)) throw std::runtime_error("WASAPI queue borrow ownership changed");
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
    std::atomic<bool> borrowed_{false};
    bool reserved_ = false; // Producer confined.
    HANDLE wake_;
#ifdef DESKFUSE_QUEUE_TEST_PROBE
    EmptyProbe empty_probe_ = nullptr;
    void* empty_probe_arg_ = nullptr;
#endif
};
} // namespace df_wasapi
