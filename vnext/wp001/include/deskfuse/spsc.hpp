#pragma once
#include "deskfuse/frame.hpp"
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace df {

// Single producer owns head; single consumer owns tail. Neither may advance
// the other's index. Source slots cannot be overwritten while consumed.
// Thread transitions: producer fills free slot, then release-publishes head;
// consumer acquire-reads head, processes immutable slot, release-publishes tail;
// producer acquire-reads tail before reuse.
template<std::size_t Capacity>
class SpscBlocks final {
    static_assert(Capacity > 0 && Capacity <= 1024);
    static_assert((Capacity & (Capacity - 1)) == 0, "Capacity must be a power of two for modulo-wrap correctness");
public:
    static constexpr std::size_t capacity = Capacity;
    SpscBlocks() = default;
    SpscBlocks(const SpscBlocks&) = delete;
    SpscBlocks& operator=(const SpscBlocks&) = delete;

    // Only producer thread calls. Pending write exists until publish().
    [[nodiscard]] PcmBlock* try_reserve() noexcept {
        if (producer_reserved_) return nullptr;
        const auto head = head_.load(std::memory_order_relaxed);
        const auto tail = tail_.load(std::memory_order_acquire);
        if (head - tail >= Capacity) { full_count_.fetch_add(1, std::memory_order_relaxed); return nullptr; }
        producer_reserved_ = true;
        return &slots_[static_cast<std::size_t>(head & (Capacity - 1))];
    }

    // Only producer thread calls after successful try_reserve().
    [[nodiscard]] bool publish() noexcept {
        if (!producer_reserved_) return false;
        producer_reserved_ = false;
        const auto head = head_.load(std::memory_order_relaxed);
        head_.store(head + 1, std::memory_order_release);
        return true;
    }

    // Producer may discard an uncommitted reservation without publishing it.
    void cancel_reservation() noexcept { producer_reserved_ = false; }

    // Only consumer thread calls. Immutable until release_front().
    [[nodiscard]] const PcmBlock* try_front() noexcept {
        const auto tail = tail_.load(std::memory_order_relaxed);
        const auto head = head_.load(std::memory_order_acquire);
        if (tail == head) return nullptr;
        consumer_holds_front_ = true;
        return &slots_[static_cast<std::size_t>(tail & (Capacity - 1))];
    }

    // Only consumer thread calls; never called by producer to implement DropOldest.
    [[nodiscard]] bool release_front() noexcept {
        if (!consumer_holds_front_) return false;
        const auto tail = tail_.load(std::memory_order_relaxed);
        if (tail == head_.load(std::memory_order_acquire)) return false;
        consumer_holds_front_ = false;
        tail_.store(tail + 1, std::memory_order_release);
        return true;
    }

    [[nodiscard]] std::uint64_t full_count() const noexcept {
        return full_count_.load(std::memory_order_relaxed);
    }
    [[nodiscard]] std::uint64_t published() const noexcept { return head_.load(std::memory_order_acquire); }
    [[nodiscard]] std::uint64_t released() const noexcept { return tail_.load(std::memory_order_acquire); }

private:
    alignas(64) std::array<PcmBlock, Capacity> slots_{};
    alignas(64) std::atomic<std::uint64_t> head_{0};
    alignas(64) std::atomic<std::uint64_t> tail_{0};
    alignas(64) std::atomic<std::uint64_t> full_count_{0};
    // Producer-only, never accessed concurrently from consumer.
    bool producer_reserved_ = false;
    bool consumer_holds_front_ = false;
};

} // namespace df
