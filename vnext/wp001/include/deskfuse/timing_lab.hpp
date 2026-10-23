#pragma once
#include "deskfuse/feedback.hpp"
#include "deskfuse/spsc.hpp"
#include "deskfuse/strict_sink.hpp"
#include <array>
#include <cstdint>

namespace df {

enum class TraceKind : std::uint8_t {
    Captured, PrecopyExpired, RingFull, Published, TransportExpired,
    WrongGeneration, ReceiverAccepted, ReceiverRejected, SourceCommitted, GenerationReset
};
struct TraceEvent {
    std::uint64_t fake_ns = 0;
    std::uint64_t first = 0;
    std::uint32_t count = 0;
    TraceKind kind = TraceKind::Captured;
    Generation generation{};
};

class FixedTrace final {
public:
    static constexpr std::size_t kCapacity = 4'096;
    void add(TraceEvent event) noexcept {
        if (used_ < kCapacity) events_[used_++] = event;
        else ++overrun_;
    }
    [[nodiscard]] std::size_t count() const noexcept { return used_; }
    [[nodiscard]] std::uint64_t overrun() const noexcept { return overrun_; }
    [[nodiscard]] const TraceEvent& operator[](std::size_t n) const noexcept { return events_[n]; }
private:
    std::array<TraceEvent, kCapacity> events_{};
    std::size_t used_ = 0;
    std::uint64_t overrun_ = 0;
};

struct LabStats {
    std::uint64_t source_frames = 0;
    std::uint64_t precopy_expired_frames = 0;
    std::uint64_t ring_full_frames = 0;
    std::uint64_t published_frames = 0;
    std::uint64_t transport_expired_frames = 0;
    std::uint64_t offered_frames = 0;
    std::uint64_t received_frames = 0;
    std::uint64_t receiver_rejected_frames = 0;
    std::uint64_t wrong_epoch_frames = 0;
    std::size_t high_water_blocks = 0;
};

class TimingLab final {
public:
    explicit TimingLab(std::uint64_t age_budget_ns = 35'000'000ULL) noexcept
        : max_age_ns_(age_budget_ns) {}
    // Models actual source intervals arriving at a host callback time.
    // Can be 100ms of source frames delivered as one callback burst.
    void capture(FrameRange source, std::uint64_t callback_ns) noexcept;
    // Egress is event driven: invoked only on modeled service opportunity.
    void service(std::uint64_t now_ns) noexcept;
    void commit_receiver(std::uint64_t exclusive_frame, StrictSink::Mode mode) noexcept;
    // Changes invalidate media ownership only after modeled quiescence.
    // A source/engine epoch has its own host-time anchor. When a shared-mode
    // engine stops and later recreates/restarts, old frame->host-time fits must
    // not be extended through the idle gap. Caller has quiesced prior owners.
    void reset(Generation generation, std::uint64_t receiver_frontier,
        std::uint64_t source_epoch_start_host_ns = 0) noexcept;
    [[nodiscard]] const LabStats& stats() const noexcept { return stats_; }
    [[nodiscard]] const FixedTrace& trace() const noexcept { return trace_; }
    [[nodiscard]] const StrictSink& sink() const noexcept { return sink_; }
    [[nodiscard]] std::uint64_t pending_blocks() const noexcept { return queue_.published() - queue_.released(); }
    [[nodiscard]] std::uint64_t ring_full_events() const noexcept { return queue_.full_count(); }

private:
    SpscBlocks<8> queue_{};
    StrictSink sink_{};
    FixedTrace trace_{};
    LabStats stats_{};
    Generation generation_{};
    std::uint64_t max_age_ns_ = 35'000'000ULL;
    std::uint64_t source_epoch_start_host_ns_ = 0;
    void emit(TraceKind kind, std::uint64_t time, FrameRange range) noexcept;
    [[nodiscard]] bool expired(std::uint64_t source_frame, std::uint64_t now) const noexcept;
};

} // namespace df
