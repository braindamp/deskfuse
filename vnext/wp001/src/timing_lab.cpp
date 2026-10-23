#include "deskfuse/timing_lab.hpp"
#include "deskfuse/pattern.hpp"
#include <algorithm>
#include <limits>

namespace df {

void TimingLab::emit(TraceKind kind, std::uint64_t time, FrameRange range) noexcept {
    trace_.add({time, range.first, range.count, kind, generation_});
}

bool TimingLab::expired(std::uint64_t frame, std::uint64_t now) const noexcept {
    const auto source_ns = source_time_ns(frame);
    if (!source_ns ||
        *source_ns > std::numeric_limits<std::uint64_t>::max() - source_epoch_start_host_ns_)
        return true;
    const auto source_host_ns = source_epoch_start_host_ns_ + *source_ns;
    if (now < source_host_ns) return false;
    return now - source_host_ns >= max_age_ns_;
}

void TimingLab::capture(FrameRange source, std::uint64_t callback_ns) noexcept {
    if (!source.valid()) return;
    stats_.source_frames += source.count;
    emit(TraceKind::Captured, callback_ns, source);
    auto pos = source.first;
    auto left = source.count;
    while (left) {
        const auto chunk_count = std::min<std::uint32_t>(left, kMaxBlockFrames);
        FrameRange chunk{pos, chunk_count};
        while (chunk.count && expired(chunk.first, callback_ns)) {
            ++chunk.first;
            --chunk.count;
            ++stats_.precopy_expired_frames;
        }
        if (chunk.count == 0) {
            emit(TraceKind::PrecopyExpired, callback_ns, {pos, chunk_count});
        } else {
            if (chunk.first != pos) emit(TraceKind::PrecopyExpired, callback_ns,
                {pos, static_cast<std::uint32_t>(chunk.first - pos)});
            auto* out = queue_.try_reserve();
            if (!out) {
                stats_.ring_full_frames += chunk.count;
                emit(TraceKind::RingFull, callback_ns, chunk);
            } else {
                (void)fill_pattern(*out, generation_, chunk);
                (void)queue_.publish();
                stats_.published_frames += chunk.count;
                emit(TraceKind::Published, callback_ns, chunk);
                const auto occupied = static_cast<std::size_t>(pending_blocks());
                stats_.high_water_blocks = std::max(stats_.high_water_blocks, occupied);
            }
        }
        pos += chunk_count;
        left -= chunk_count;
    }
}

void TimingLab::service(std::uint64_t now_ns) noexcept {
    // In this first deterministic experiment one service opportunity can drain
    // source frames, but it never invents a 10ms cadence or ages data as new.
    while (const auto* block = queue_.try_front()) {
        if (block->generation != generation_) {
            stats_.wrong_epoch_frames += block->range.count;
            emit(TraceKind::WrongGeneration, now_ns, block->range);
            (void)queue_.release_front();
            continue;
        }
        auto first = block->range.first;
        const auto end = *block->range.end();
        while (first < end && expired(first, now_ns)) ++first;
        if (first != block->range.first) {
            const auto num = static_cast<std::uint32_t>(first - block->range.first);
            stats_.transport_expired_frames += num;
            emit(TraceKind::TransportExpired, now_ns, {block->range.first, num});
        }
        while (first < end) {
            const auto count = static_cast<std::uint32_t>(std::min<std::uint64_t>(240ULL, end - first));
            PcmBlock packet{};
            packet.generation = generation_;
            packet.range = {first, count};
            const auto offset = static_cast<std::size_t>(first - block->range.first) * kChannels;
            for (std::size_t i = 0; i < static_cast<std::size_t>(count) * kChannels; ++i)
                packet.pcm[i] = block->pcm[offset + i];
            stats_.offered_frames += count;
            const auto result = sink_.receive(packet);
            if (result == StoreResult::Accepted) {
                stats_.received_frames += count;
                emit(TraceKind::ReceiverAccepted, now_ns, packet.range);
            } else {
                stats_.receiver_rejected_frames += count;
                emit(TraceKind::ReceiverRejected, now_ns, packet.range);
            }
            first += count;
        }
        (void)queue_.release_front();
    }
}

void TimingLab::commit_receiver(std::uint64_t end, StrictSink::Mode mode) noexcept {
    const auto from = sink_.sealed_frontier();
    if (sink_.commit_until(end, mode))
        emit(TraceKind::SourceCommitted, 0, {from, static_cast<std::uint32_t>(end - from)});
}

void TimingLab::reset(Generation generation, std::uint64_t receiver_frontier,
    std::uint64_t source_epoch_start_host_ns) noexcept {
    // A test-only quiescent reset; production cannot reset while threads own
    // slot pointers. Simulated new generation frames cannot revive old ones.
    generation_ = generation;
    source_epoch_start_host_ns_ = source_epoch_start_host_ns;
    sink_.reset(generation, receiver_frontier);
    while (queue_.try_front()) (void)queue_.release_front();
    emit(TraceKind::GenerationReset, 0, {receiver_frontier, 0});
}

} // namespace df
