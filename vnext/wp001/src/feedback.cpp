#include "deskfuse/feedback.hpp"
#include <limits>

namespace df {

void FeedbackGate::reset(Generation generation) noexcept {
    generation_ = generation;
    last_sequence_ = last_publication_receiver_ns_ = frontier_ = accept_hi_ = expiry_ = 0;
    valid_ = false;
}

FeedbackResult FeedbackGate::update(const ReceiverFeedback& f,
    std::uint64_t now, std::int64_t receiver_minus_host) noexcept {
    if (f.generation != generation_) return FeedbackResult::WrongGeneration;
    if (f.sequence == 0 || (valid_ && f.sequence <= last_sequence_)) return FeedbackResult::OldSequence;
    if (f.accept_hi < f.sealed_frontier || f.accept_hi - f.sealed_frontier > 4'800ULL ||
        f.lease_duration_ns == 0 || f.lease_duration_ns > 1'000'000'000ULL)
        return FeedbackResult::InvalidBounds;
    if (valid_ && f.sealed_frontier < frontier_) return FeedbackResult::RegressedFrontier;
    // Latest-state feedback can arrive out of order even with a higher sequence.
    // A newer sequence must not revive an older publication snapshot.
    if (valid_ && f.publication_receiver_ns < last_publication_receiver_ns_)
        return FeedbackResult::RegressedPublicationTime;

    std::uint64_t published_host = f.publication_receiver_ns;
    if (receiver_minus_host >= 0) {
        const auto bias = static_cast<std::uint64_t>(receiver_minus_host);
        if (published_host < bias) return FeedbackResult::InvalidTimestamp;
        published_host -= bias;
    } else {
        // Avoid negating INT64_MIN as signed integer.
        const auto bias = static_cast<std::uint64_t>(-(receiver_minus_host + 1)) + 1ULL;
        if (published_host > std::numeric_limits<std::uint64_t>::max() - bias)
            return FeedbackResult::InvalidTimestamp;
        published_host += bias;
    }
    if (published_host > now || published_host > std::numeric_limits<std::uint64_t>::max() - f.lease_duration_ns)
        return FeedbackResult::InvalidTimestamp;
    const auto expiry = published_host + f.lease_duration_ns;
    if (now >= expiry) return FeedbackResult::ExpiredOnArrival;

    valid_ = true;
    last_sequence_ = f.sequence;
    last_publication_receiver_ns_ = f.publication_receiver_ns;
    frontier_ = f.sealed_frontier;
    accept_hi_ = f.accept_hi;
    expiry_ = expiry;
    return FeedbackResult::Accepted;
}

bool FeedbackGate::permits(Generation generation, FrameRange range, std::uint64_t now) const noexcept {
    if (!valid_ || generation != generation_ || now >= expiry_ || !range.valid()) return false;
    return range.first >= frontier_ && *range.end() <= accept_hi_;
}

} // namespace df
