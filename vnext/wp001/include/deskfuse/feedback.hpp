#pragma once
#include "deskfuse/frame.hpp"
#include <cstdint>

namespace df {

// Receiver-origin, replaceable latest-state record. Units always nanoseconds
// on a declared receiver-monotonic clock, never time-since-arrival.
struct ReceiverFeedback {
    Generation generation{};
    std::uint64_t sequence = 0;
    std::uint64_t publication_receiver_ns = 0;
    std::uint64_t sealed_frontier = 0;
    std::uint64_t accept_hi = 0;
    std::uint64_t lease_duration_ns = 250'000'000;
};

enum class FeedbackResult : std::uint8_t {
    Accepted, WrongGeneration, OldSequence, InvalidBounds,
    ExpiredOnArrival, InvalidTimestamp, RegressedFrontier, RegressedPublicationTime
};

class FeedbackGate final {
public:
    explicit FeedbackGate(Generation generation = {}) noexcept : generation_(generation) {}
    void reset(Generation generation) noexcept;
    // receiver_minus_host_ns is an already calibrated offset for this model.
    // Production transport must use its measured uncertain clock map and
    // conservative expiry, not blindly trust a peer supplied offset.
    [[nodiscard]] FeedbackResult update(const ReceiverFeedback& feedback,
        std::uint64_t now_host_ns, std::int64_t receiver_minus_host_ns = 0) noexcept;
    [[nodiscard]] bool permits(Generation generation, FrameRange range,
        std::uint64_t now_host_ns) const noexcept;
    [[nodiscard]] std::uint64_t lease_expiry_host_ns() const noexcept { return expiry_; }
    [[nodiscard]] std::uint64_t last_sequence() const noexcept { return last_sequence_; }
    [[nodiscard]] std::uint64_t last_publication_receiver_ns() const noexcept { return last_publication_receiver_ns_; }
    [[nodiscard]] bool valid() const noexcept { return valid_; }

private:
    Generation generation_{};
    std::uint64_t last_sequence_ = 0;
    std::uint64_t last_publication_receiver_ns_ = 0;
    std::uint64_t frontier_ = 0;
    std::uint64_t accept_hi_ = 0;
    std::uint64_t expiry_ = 0;
    bool valid_ = false;
};

} // namespace df
