#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
namespace df {
// Deterministic test-only event scheduler. Sorted by virtual time and insertion
// sequence; no thread sleeps, no mandatory 10ms callback cadence.
struct FakeEvent {
    enum class Kind : std::uint8_t { Capture, Egress, Commit, Reset };
    std::uint64_t at_ns = 0;
    std::uint64_t argument = 0;
    std::uint64_t insertion_sequence = 0;
    Kind kind = Kind::Capture;
};
template<std::size_t Capacity>
class FakeScheduler {
public:
    [[nodiscard]] bool add(FakeEvent event) noexcept {
        if (used_ == Capacity) return false;
        event.insertion_sequence = next_++;
        events_[used_++] = event;
        return true;
    }
    [[nodiscard]] std::optional<FakeEvent> next() noexcept {
        if (!used_) return std::nullopt;
        std::size_t best = 0;
        for (std::size_t i = 1; i < used_; ++i) {
            if (events_[i].at_ns < events_[best].at_ns ||
                (events_[i].at_ns == events_[best].at_ns &&
                 events_[i].insertion_sequence < events_[best].insertion_sequence)) best = i;
        }
        const auto out = events_[best];
        events_[best] = events_[--used_];
        return out;
    }
    [[nodiscard]] std::size_t size() const noexcept { return used_; }
private:
    std::array<FakeEvent, Capacity> events_{};
    std::size_t used_ = 0;
    std::uint64_t next_ = 0;
};
} // namespace df
