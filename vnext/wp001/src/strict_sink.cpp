#include "deskfuse/strict_sink.hpp"
#include "deskfuse/pattern.hpp"
#include <limits>

namespace df {

void StrictSink::reset(Generation generation, std::uint64_t new_frontier) noexcept {
    generation_ = generation;
    sealed_ = new_frontier;
    occupancy_ = 0;
    stats_ = {};
    for (auto& slot : slots_) slot.occupied = false;
}

StoreResult StrictSink::receive(const PcmBlock& block) noexcept {
    const auto reject = [this](StoreResult why) noexcept {
        ++stats_.rejected;
        if (why == StoreResult::CorruptPcm) ++stats_.corruption;
        return why;
    };
    if (!block.range.valid() || block.range.count > kMaxBlockFrames) return reject(StoreResult::Invalid);
    if (block.generation != generation_) return reject(StoreResult::WrongGeneration);
    if (block.range.first < sealed_) return reject(StoreResult::Late);
    const auto end = *block.range.end();
    if (end - sealed_ > kHorizonFrames) return reject(StoreResult::Future);
    if (!verify_pattern(block.pcm, block.range)) return reject(StoreResult::CorruptPcm);
    for (const auto& slot : slots_) {
        if (slot.occupied && overlaps(slot.block.range, block.range)) return reject(StoreResult::DuplicateOrOverlap);
    }
    for (auto& slot : slots_) {
        if (!slot.occupied) {
            slot.block = block;
            slot.occupied = true;
            ++occupancy_;
            if (occupancy_ > stats_.high_water) stats_.high_water = occupancy_;
            stats_.received_frames += block.range.count;
            return StoreResult::Accepted;
        }
    }
    return reject(StoreResult::Full);
}

const PcmBlock* StrictSink::covering(std::uint64_t frame) const noexcept {
    for (const auto& slot : slots_) {
        if (slot.occupied && slot.block.range.first <= frame && frame < *slot.block.range.end())
            return &slot.block;
    }
    return nullptr;
}

void StrictSink::hash_sample(std::int16_t sample) noexcept {
    const auto bits = static_cast<std::uint16_t>(sample);
    stats_.rolling_hash ^= static_cast<std::uint8_t>(bits);
    stats_.rolling_hash *= 1099511628211ULL;
    stats_.rolling_hash ^= static_cast<std::uint8_t>(bits >> 8U);
    stats_.rolling_hash *= 1099511628211ULL;
}

void StrictSink::release_committed() noexcept {
    for (auto& slot : slots_) {
        if (slot.occupied && *slot.block.range.end() <= sealed_) {
            slot.occupied = false;
            --occupancy_;
        }
    }
}

bool StrictSink::commit_until(std::uint64_t exclusive_end, Mode mode) noexcept {
    if (exclusive_end < sealed_ || exclusive_end - sealed_ > kHorizonFrames) return false;
    // Preflight strict mode: no partial commitment after an encountered hole.
    if (mode == Mode::StrictStop) {
        for (auto frame = sealed_; frame < exclusive_end; ++frame) {
            if (covering(frame) == nullptr) return false;
        }
    }
    while (sealed_ < exclusive_end) {
        const auto* block = covering(sealed_);
        if (block) {
            const std::size_t offset = static_cast<std::size_t>(sealed_ - block->range.first) * kChannels;
            for (std::uint32_t c = 0; c < kChannels; ++c) hash_sample(block->pcm[offset + c]);
            ++stats_.real_frames;
        } else {
            // Missing interval is explicitly sealed. Originals arriving later
            // are rejected and cannot rewrite the selected PLC/silence input.
            ++stats_.missing_frames;
        }
        ++sealed_;
    }
    release_committed();
    return true;
}

} // namespace df
