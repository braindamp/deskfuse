#include "deskfuse/feedback.hpp"
#include "deskfuse/pattern.hpp"
#include "deskfuse/reference_control.hpp"
#include "deskfuse/scheduler.hpp"
#include "deskfuse/spsc.hpp"
#include "deskfuse/strict_sink.hpp"
#include "deskfuse/timing_lab.hpp"
#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
using namespace df;
#define CHECK(...) do { if (!(__VA_ARGS__)) throw std::runtime_error(\
    std::string("CHECK failed: ") + #__VA_ARGS__ + " line=" + std::to_string(__LINE__)); } while (0)

PcmBlock pattern(std::uint64_t first, std::uint32_t count = 240, Generation g = {}) {
    PcmBlock b{};
    CHECK(fill_pattern(b, g, {first, count}));
    return b;
}

void frames_and_arithmetic() {
    CHECK(FrameRange{0, 480}.valid());
    CHECK(*FrameRange{480, 480}.end() == 960);
    CHECK(!FrameRange{std::numeric_limits<std::uint64_t>::max(), 2}.valid());
    CHECK(!FrameRange{0, 0}.valid());
    CHECK(overlaps({100, 50}, {149, 20}));
    CHECK(!overlaps({100, 50}, {150, 1}));
    CHECK(*bytes_for_frames({10, 240}, 2, 2) == 960);
    CHECK(!bytes_for_frames({0, 1}, 0, 2));
    CHECK(bytes_for_frames({0, 1}, std::numeric_limits<std::uint32_t>::max(),
                            std::numeric_limits<std::uint32_t>::max()).has_value()); // product fits u64
    CHECK(source_time_ns(48'000) == 1'000'000'000ULL);
    CHECK(source_time_ns(240) == 5'000'000ULL);
    CHECK(source_time_ns(4'800) == 100'000'000ULL);
    CHECK(!source_time_ns(std::numeric_limits<std::uint64_t>::max()));
}

void sample_identity_and_channels() {
    auto b = pattern(42, 480);
    CHECK(verify_pattern(b.pcm, b.range));
    CHECK(pattern_sample(42, 0) != pattern_sample(42, 1));
    b.pcm[17] ^= 1;
    CHECK(!verify_pattern(b.pcm, b.range));
    CHECK(!fill_pattern(b, {}, {0, 481}));
}

void strict_sink_exact_and_hash() {
    StrictSink sink;
    const auto a = pattern(0), b = pattern(240), c = pattern(480);
    CHECK(sink.receive(b) == StoreResult::Accepted); // out-of-order arrival
    CHECK(sink.receive(a) == StoreResult::Accepted);
    CHECK(sink.receive(c) == StoreResult::Accepted);
    CHECK(sink.commit_until(720, StrictSink::Mode::StrictStop));
    CHECK(sink.stats().real_frames == 720);
    CHECK(sink.stats().missing_frames == 0);
    CHECK(sink.occupancy() == 0);
    // Hash independent of arrival ordering, fixed little-endian PCM16 samples.
    std::uint64_t hash = 14695981039346656037ULL;
    for (std::uint64_t n = 0; n < 720; ++n) {
        for (std::uint32_t ch = 0; ch < kChannels; ++ch) {
            const auto sample = static_cast<std::uint16_t>(pattern_sample(n, ch));
            hash = (hash ^ static_cast<std::uint8_t>(sample)) * 1099511628211ULL;
            hash = (hash ^ static_cast<std::uint8_t>(sample >> 8U)) * 1099511628211ULL;
        }
    }
    CHECK(sink.stats().rolling_hash == hash);
}

void sink_missing_is_irreversible() {
    StrictSink sink;
    CHECK(sink.receive(pattern(240)) == StoreResult::Accepted);
    CHECK(!sink.commit_until(480, StrictSink::Mode::StrictStop));
    CHECK(sink.sealed_frontier() == 0);
    CHECK(sink.commit_until(480, StrictSink::Mode::AccountMissing));
    CHECK(sink.stats().missing_frames == 240);
    CHECK(sink.stats().real_frames == 240);
    CHECK(sink.receive(pattern(0)) == StoreResult::Late); // no post-PLC rewrite
}

void sink_invalid_and_replay() {
    StrictSink sink;
    auto wrong = pattern(0, 240, {999, 1, 1});
    CHECK(sink.receive(wrong) == StoreResult::WrongGeneration);
    auto good = pattern(0);
    CHECK(sink.receive(good) == StoreResult::Accepted);
    CHECK(sink.receive(good) == StoreResult::DuplicateOrOverlap);
    CHECK(sink.receive(pattern(120)) == StoreResult::DuplicateOrOverlap);
    CHECK(sink.receive(pattern(4'800, 240)) == StoreResult::Future);
    auto bad = pattern(240);
    bad.pcm[0] ^= 1;
    CHECK(sink.receive(bad) == StoreResult::CorruptPcm);
    CHECK(sink.stats().corruption == 1);
    CHECK(sink.stats().high_water == 1);
    sink.reset({2, 2, 2}, 5'000);
    CHECK(sink.receive(pattern(0)) == StoreResult::WrongGeneration);
    CHECK(sink.receive(pattern(5'000, 240, {2,2,2})) == StoreResult::Accepted);
    CHECK(sink.commit_until(5'240, StrictSink::Mode::StrictStop));
}

void sink_capacity_is_shared() {
    StrictSink sink;
    for (std::uint64_t n = 0; n < 4'800; n += 240) CHECK(sink.receive(pattern(n)) == StoreResult::Accepted);
    CHECK(sink.occupancy() == 20);
    CHECK(sink.stats().high_water == 20);
    CHECK(sink.receive(pattern(0)) == StoreResult::DuplicateOrOverlap);
    CHECK(sink.commit_until(4'800, StrictSink::Mode::StrictStop));
    CHECK(sink.occupancy() == 0);
}

void ring_drop_new_not_consumer_tail() {
    SpscBlocks<8> q;
    auto* first = q.try_reserve(); CHECK(first != nullptr);
    CHECK(fill_pattern(*first, {}, {0, 32})); CHECK(q.publish());
    auto* held = q.try_front(); CHECK(held != nullptr);
    CHECK(held->range.first == 0);
    for (std::uint64_t i = 1; i < 8; ++i) {
        auto* slot = q.try_reserve(); CHECK(slot != nullptr);
        CHECK(fill_pattern(*slot, {}, {i * 32, 32})); CHECK(q.publish());
    }
    CHECK(q.try_reserve() == nullptr);
    CHECK(q.full_count() == 1);
    CHECK(held->range.first == 0);
    CHECK(verify_pattern(held->pcm, held->range));
    CHECK(q.release_front());
    auto* slot = q.try_reserve(); CHECK(slot != nullptr);
    CHECK(fill_pattern(*slot, {}, {8 * 32, 32})); CHECK(q.publish());
    for (std::uint64_t i = 1; i <= 8; ++i) {
        auto* front = q.try_front(); CHECK(front != nullptr);
        CHECK(front->range.first == i * 32);
        CHECK(verify_pattern(front->pcm, front->range));
        CHECK(q.release_front());
    }
    CHECK(q.try_front() == nullptr);
    CHECK(!q.release_front()); // Double-release must never drop the next slot.
    CHECK(q.published() == q.released());
}

void ring_reservation_cancel() {
    SpscBlocks<2> q;
    auto* first = q.try_reserve(); CHECK(first != nullptr);
    CHECK(q.try_reserve() == nullptr);
    q.cancel_reservation();
    first = q.try_reserve(); CHECK(first != nullptr);
    CHECK(fill_pattern(*first, {}, {20, 10}));
    CHECK(q.publish());
    CHECK(q.try_front() != nullptr);
    CHECK(q.release_front());
}

void feedback_lease_is_publication_bound() {
    FeedbackGate gate;
    ReceiverFeedback f{};
    f.sequence = 1;
    f.publication_receiver_ns = 1'000'000'000;
    f.sealed_frontier = 1'000;
    f.accept_hi = 5'000;
    f.lease_duration_ns = 250'000'000;
    CHECK(gate.update(f, 1'050'000'000) == FeedbackResult::Accepted);
    CHECK(gate.permits({}, {1'100, 240}, 1'249'999'999));
    CHECK(!gate.permits({}, {1'100, 240}, 1'250'000'000));
    auto delayed = f; delayed.sequence = 2;
    CHECK(gate.update(delayed, 1'500'000'000) == FeedbackResult::ExpiredOnArrival);
    CHECK(!gate.permits({}, {1'100, 240}, 1'500'000'000));
    // Delayed arrival never grants a new lease measured from arrival.
    CHECK(gate.lease_expiry_host_ns() == 1'250'000'000ULL);
    delayed.publication_receiver_ns = 1'490'000'000;
    CHECK(gate.update(delayed, 1'500'000'000) == FeedbackResult::Accepted);
    CHECK(gate.lease_expiry_host_ns() == 1'740'000'000);
    CHECK(gate.update(f, 1'510'000'000) == FeedbackResult::OldSequence);
    CHECK(gate.permits({}, {4'750, 240}, 1'510'000'000));
    CHECK(!gate.permits({}, {4'800, 240}, 1'510'000'000));
}

void feedback_epochs_and_clock_offset() {
    FeedbackGate gate;
    ReceiverFeedback f{};
    f.sequence = 5; f.publication_receiver_ns = 1'100'000'000;
    f.accept_hi = 4'800;
    CHECK(gate.update(f, 1'050'000'000, 100'000'000) == FeedbackResult::Accepted);
    CHECK(gate.lease_expiry_host_ns() == 1'250'000'000);
    f.sequence = 6; f.sealed_frontier = 10; f.accept_hi = 4'800;
    CHECK(gate.update(f, 1'080'000'000, 100'000'000) == FeedbackResult::Accepted);
    f.sequence = 7; f.sealed_frontier = 0;
    CHECK(gate.update(f, 1'090'000'000, 100'000'000) == FeedbackResult::RegressedFrontier);
    f.sequence = 8; f.generation.source = 22;
    CHECK(gate.update(f, 1'090'000'000, 100'000'000) == FeedbackResult::WrongGeneration);
    gate.reset({22, 2, 5});
    CHECK(!gate.permits({}, {10, 10}, 1'090'000'000));
    f.generation = {22, 2, 5}; f.sequence = 1;
    f.publication_receiver_ns = 1'200'000'000; f.sealed_frontier=0;
    CHECK(gate.update(f, 1'110'000'000, 100'000'000) == FeedbackResult::Accepted);
}

void feedback_rejects_regressed_publication_even_if_sequence_increases() {
    FeedbackGate gate;
    ReceiverFeedback f{};
    f.sequence = 10;
    f.publication_receiver_ns = 1'000'000'000;
    f.sealed_frontier = 120;
    f.accept_hi = 4'800;
    CHECK(gate.update(f, 1'010'000'000) == FeedbackResult::Accepted);
    CHECK(gate.last_sequence() == 10);
    CHECK(gate.last_publication_receiver_ns() == 1'000'000'000);
    auto older = f;
    older.sequence = 12;
    older.publication_receiver_ns = 990'000'000;
    CHECK(gate.update(older, 1'020'000'000) == FeedbackResult::RegressedPublicationTime);
    CHECK(gate.last_sequence() == 10);
    CHECK(gate.lease_expiry_host_ns() == 1'250'000'000);
    older.publication_receiver_ns = 1'020'000'000;
    CHECK(gate.update(older, 1'030'000'000) == FeedbackResult::Accepted);
    CHECK(gate.last_sequence() == 12);
    CHECK(gate.last_publication_receiver_ns() == 1'020'000'000);
    // A newer sequence with a delayed, already-expired publication is not a new lease.
    older.sequence = 13;
    older.publication_receiver_ns = 1'021'000'000;
    CHECK(gate.update(older, 1'500'000'000) == FeedbackResult::ExpiredOnArrival);
    CHECK(gate.last_sequence() == 12);
    CHECK(!gate.permits({}, {240, 240}, 1'500'000'000));
}

void control_sign_and_clock_drift() {
    CHECK(ReferenceControl::ratio(48'000, 48'000, +0.010) > 1.0);
    CHECK(ReferenceControl::ratio(48'000, 48'000, -0.010) < 1.0);
    CHECK(ReferenceControl::inventory_change_frames_per_s(48'000, 48'000, 1.0001) < 0.0);
    CHECK(ReferenceControl::inventory_change_frames_per_s(48'000, 48'000, 0.9999) > 0.0);
    CHECK(ReferenceControl::ratio(48'004.8, 48'000, 0.0) > 1.0); // +100ppm source
    CHECK(ReferenceControl::ratio(47'995.2, 48'000, 0.0) < 1.0); // -100ppm source
    CHECK(ReferenceControl::ratio(48'000, 48'000, 1.0) <= 1.000200001);
    CHECK(ReferenceControl::ratio(48'000, 48'000, -1.0) >= 0.999799999);
}

void fake_schedule_is_not_a_metronome() {
    FakeScheduler<8> s;
    CHECK(s.add({100, 1, 0, FakeEvent::Kind::Capture}));
    CHECK(s.add({100, 2, 0, FakeEvent::Kind::Egress}));
    CHECK(s.add({0, 3, 0, FakeEvent::Kind::Reset}));
    CHECK(s.next()->argument == 3);
    CHECK(s.next()->argument == 1);
    CHECK(s.next()->argument == 2);
    CHECK(!s.next().has_value());
}

void normal_delivery_all_exact() {
    TimingLab lab;
    for (std::uint64_t i = 0; i < 10; ++i) {
        lab.capture({i * 480, 480}, (i+1) * 10'000'000ULL);
        lab.service((i+1) * 10'000'000ULL);
        lab.commit_receiver((i+1) * 480, StrictSink::Mode::StrictStop);
    }
    CHECK(lab.stats().source_frames == 4'800);
    CHECK(lab.stats().received_frames == 4'800);
    CHECK(lab.sink().stats().real_frames == 4'800);
    CHECK(lab.sink().stats().missing_frames == 0);
    CHECK(lab.trace().overrun() == 0);
}

void delayed_burst_keeps_original_frames() {
    TimingLab lab;
    lab.capture({0, 4'800}, 100'000'000);
    lab.service(100'000'000);
    lab.commit_receiver(4'800, StrictSink::Mode::AccountMissing);
    const auto& s = lab.stats();
    CHECK(s.source_frames == 4'800);
    CHECK(s.precopy_expired_frames >= 3'100);
    CHECK(s.received_frames > 0 && s.received_frames < 4'800);
    CHECK(lab.sink().stats().real_frames == s.received_frames);
    CHECK(lab.sink().stats().real_frames + lab.sink().stats().missing_frames == 4'800);
    CHECK(s.high_water_blocks <= 8);
}

void blocked_egress_discards_expired_not_retimestamp() {
    TimingLab lab;
    for (std::uint64_t i = 0; i < 10; ++i)
        lab.capture({i * 480, 480}, (i+1) * 10'000'000ULL);
    CHECK(lab.stats().ring_full_frames == 960);
    CHECK(lab.pending_blocks() == 8);
    lab.service(100'000'000ULL);
    lab.commit_receiver(4'800, StrictSink::Mode::AccountMissing);
    CHECK(lab.stats().transport_expired_frames > 0);
    CHECK(lab.sink().stats().missing_frames > 0);
    CHECK(lab.pending_blocks() == 0);
    CHECK(lab.stats().high_water_blocks <= 8);
}

void source_idle_and_restarted_engine_epoch() {
    TimingLab lab;
    lab.capture({0, 480}, 10'000'000ULL);
    lab.service(10'000'000ULL);
    lab.commit_receiver(480, StrictSink::Mode::StrictStop);
    CHECK(lab.sink().stats().real_frames == 480);
    // No engine event: waiting does not create 48000 * wall-time synthetic frames.
    lab.service(510'000'000ULL);
    CHECK(lab.stats().source_frames == 480);
    CHECK(lab.stats().received_frames == 480);
    CHECK(lab.sink().sealed_frontier() == 480);
    // An engine reinitialization with reset frame coordinates and a new QPC
    // anchor is a NEW source generation, not a 500ms backlog to retransmit.
    lab.reset({2, 2, 2}, 0, 500'000'000ULL);
    lab.capture({0, 480}, 510'000'000ULL);
    lab.service(510'000'000ULL);
    lab.commit_receiver(480, StrictSink::Mode::StrictStop);
    CHECK(lab.sink().stats().real_frames == 480);
    CHECK(lab.sink().stats().missing_frames == 0);
    CHECK(lab.stats().source_frames == 960);
    CHECK(lab.stats().received_frames == 960);
    CHECK(lab.pending_blocks() == 0);
}

void feedback_rejects_future_and_invalid_ranges() {
    FeedbackGate gate;
    ReceiverFeedback f{};
    f.sequence = 1;
    f.publication_receiver_ns = 1'000'000'001;
    f.accept_hi = 4800;
    CHECK(gate.update(f, 1'000'000'000ULL) == FeedbackResult::InvalidTimestamp);
    f.publication_receiver_ns = 1'000'000'000;
    f.accept_hi = 4801;
    CHECK(gate.update(f, 1'000'000'000ULL) == FeedbackResult::InvalidBounds);
    f.accept_hi = 4800;
    CHECK(gate.update(f, 1'001'000'000ULL) == FeedbackResult::Accepted);
    CHECK(gate.permits({}, {1, 240}, 1'001'000'000ULL));
    auto wrong = f;
    wrong.sequence = 2;
    wrong.generation.connection += 1;
    CHECK(gate.update(wrong, 1'002'000'000ULL) == FeedbackResult::WrongGeneration);
    CHECK(gate.last_sequence() == 1);
}

void late_generation_and_stale_sink() {
    StrictSink sink;
    CHECK(sink.receive(pattern(0)) == StoreResult::Accepted);
    sink.reset({2,2,2}, 1'000);
    CHECK(sink.receive(pattern(0)) == StoreResult::WrongGeneration);
    CHECK(sink.receive(pattern(1'000, 240, {2,2,2})) == StoreResult::Accepted);
    CHECK(sink.commit_until(1'240, StrictSink::Mode::StrictStop));
    CHECK(sink.receive(pattern(1'000, 240, {2,2,2})) == StoreResult::Late);
}

void real_two_thread_spsc_stress() {
    constexpr std::uint64_t blocks = 65'536;
    constexpr std::uint32_t frames = 32;
    SpscBlocks<8> q;
    std::atomic<bool> start{false};
    std::atomic<std::uint64_t> errors{0};
    std::thread producer([&]() {
        while (!start.load(std::memory_order_acquire)) std::this_thread::yield();
        for (std::uint64_t i = 0; i < blocks; ++i) {
            PcmBlock* slot = nullptr;
            while ((slot = q.try_reserve()) == nullptr) std::this_thread::yield();
            if (!fill_pattern(*slot, {}, {i * frames, frames})) ++errors;
            if (!q.publish()) ++errors;
            if ((i % 17) == 0) std::this_thread::yield();
        }
    });
    std::thread consumer([&]() {
        while (!start.load(std::memory_order_acquire)) std::this_thread::yield();
        for (std::uint64_t i = 0; i < blocks; ++i) {
            const PcmBlock* slot = nullptr;
            while ((slot = q.try_front()) == nullptr) std::this_thread::yield();
            if (slot->range.first != i * frames || slot->range.count != frames ||
                !verify_pattern(slot->pcm, slot->range)) ++errors;
            if ((i % 19) == 0) std::this_thread::yield(); // hold immutable front under pressure
            if (!q.release_front()) ++errors;
        }
    });
    start.store(true, std::memory_order_release);
    producer.join();
    consumer.join();
    CHECK(errors.load() == 0);
    CHECK(q.published() == blocks);
    CHECK(q.released() == blocks);
    CHECK(q.try_front() == nullptr);
}

void real_two_thread_drop_on_full() {
    constexpr std::uint64_t attempts = 30'000;
    SpscBlocks<8> q;
    std::atomic<bool> start{false};
    std::atomic<bool> producer_done{false};
    std::atomic<std::uint64_t> dropped{0};
    std::atomic<std::uint64_t> checked{0};
    std::atomic<std::uint64_t> errors{0};
    std::thread producer([&]() {
        while (!start.load(std::memory_order_acquire)) std::this_thread::yield();
        for (std::uint64_t i = 0; i < attempts; ++i) {
            if (auto* slot = q.try_reserve()) {
                if (!fill_pattern(*slot, {}, {i * 16, 16})) ++errors;
                if (!q.publish()) ++errors;
            } else ++dropped; // Never steal consumer tail.
        }
        producer_done.store(true, std::memory_order_release);
    });
    std::thread consumer([&]() {
        while (!start.load(std::memory_order_acquire)) std::this_thread::yield();
        std::uint64_t previous = 0;
        bool seen = false;
        while (!producer_done.load(std::memory_order_acquire) || q.try_front() != nullptr) {
            const auto* block = q.try_front();
            if (!block) { std::this_thread::yield(); continue; }
            if (!verify_pattern(block->pcm, block->range) ||
                (seen && block->range.first <= previous)) ++errors;
            previous = block->range.first;
            seen = true;
            ++checked;
            if (!q.release_front()) ++errors;
            if ((checked.load() % 3) == 0) std::this_thread::yield();
        }
    });
    start.store(true, std::memory_order_release);
    producer.join(); consumer.join();
    CHECK(errors.load() == 0);
    CHECK(checked.load() + dropped.load() == attempts);
    CHECK(q.full_count() == dropped.load());
    CHECK(q.published() == q.released());
}

void concurrent_held_front_cannot_be_overwritten() {
    // Forces actual cross-thread ownership pressure while the consumer is
    // inspecting immutable PCM. The producer must never reclaim that slot.
    SpscBlocks<8> q;
    std::atomic<unsigned> stage{0};
    std::atomic<std::uint64_t> errors{0};
    std::thread producer([&]() {
        auto* slot = q.try_reserve();
        if (!slot || !fill_pattern(*slot, {}, {0, 32}) || !q.publish()) ++errors;
        stage.store(1, std::memory_order_release);
        while (stage.load(std::memory_order_acquire) < 2) std::this_thread::yield();
        for (std::uint64_t i = 1; i < 8; ++i) {
            slot = q.try_reserve();
            if (!slot || !fill_pattern(*slot, {}, {i * 32, 32}) || !q.publish()) ++errors;
        }
        for (int i = 0; i < 5; ++i)
            if (q.try_reserve() != nullptr) ++errors;
        stage.store(3, std::memory_order_release);
        while (stage.load(std::memory_order_acquire) < 4) std::this_thread::yield();
        slot = q.try_reserve();
        if (!slot || !fill_pattern(*slot, {}, {8 * 32, 32}) || !q.publish()) ++errors;
    });
    std::thread consumer([&]() {
        while (stage.load(std::memory_order_acquire) < 1) std::this_thread::yield();
        const auto* held = q.try_front();
        if (!held || held->range.first != 0) ++errors;
        stage.store(2, std::memory_order_release);
        while (stage.load(std::memory_order_acquire) < 3) {
            if (!held || held->range.first != 0 || !verify_pattern(held->pcm, held->range)) ++errors;
            std::this_thread::yield();
        }
        if (!held || !verify_pattern(held->pcm, held->range) || !q.release_front()) ++errors;
        stage.store(4, std::memory_order_release);
    });
    producer.join();
    consumer.join();
    CHECK(errors.load() == 0);
    CHECK(q.full_count() == 5);
    for (std::uint64_t i = 1; i <= 8; ++i) {
        const auto* block = q.try_front();
        CHECK(block != nullptr);
        CHECK(block->range.first == i * 32);
        CHECK(verify_pattern(block->pcm, block->range));
        CHECK(q.release_front());
    }
    CHECK(q.published() == q.released());
}

struct NamedTest {const char* name; void (*func)();};
}

int main() {
    const NamedTest tests[] = {
        {"frames_and_arithmetic", frames_and_arithmetic},
        {"sample_identity_and_channels", sample_identity_and_channels},
        {"strict_sink_exact_and_hash", strict_sink_exact_and_hash},
        {"sink_missing_is_irreversible", sink_missing_is_irreversible},
        {"sink_invalid_and_replay", sink_invalid_and_replay},
        {"sink_capacity_is_shared", sink_capacity_is_shared},
        {"ring_drop_new_not_consumer_tail", ring_drop_new_not_consumer_tail},
        {"ring_reservation_cancel", ring_reservation_cancel},
        {"feedback_lease_is_publication_bound", feedback_lease_is_publication_bound},
        {"feedback_epochs_and_clock_offset", feedback_epochs_and_clock_offset},
        {"feedback_rejects_regressed_publication_even_if_sequence_increases", feedback_rejects_regressed_publication_even_if_sequence_increases},
        {"control_sign_and_clock_drift", control_sign_and_clock_drift},
        {"fake_schedule_is_not_a_metronome", fake_schedule_is_not_a_metronome},
        {"normal_delivery_all_exact", normal_delivery_all_exact},
        {"delayed_burst_keeps_original_frames", delayed_burst_keeps_original_frames},
        {"blocked_egress_discards_expired_not_retimestamp", blocked_egress_discards_expired_not_retimestamp},
        {"late_generation_and_stale_sink", late_generation_and_stale_sink},
        {"source_idle_and_restarted_engine_epoch", source_idle_and_restarted_engine_epoch},
        {"feedback_rejects_future_and_invalid_ranges", feedback_rejects_future_and_invalid_ranges},
        {"real_two_thread_spsc_stress", real_two_thread_spsc_stress},
        {"real_two_thread_drop_on_full", real_two_thread_drop_on_full},
        {"concurrent_held_front_cannot_be_overwritten", concurrent_held_front_cannot_be_overwritten},
    };
    std::size_t failed = 0;
    for (const auto& test : tests) {
        try {test.func(); std::cout << "PASS " << test.name << '\n';}
        catch (const std::exception& ex) {++failed; std::cerr << "FAIL " << test.name << ": " << ex.what() << '\n';}
    }
    std::cout << "total=" << std::size(tests) << " passed=" << (std::size(tests) - failed)
              << " failed=" << failed << '\n';
    return failed ? 1 : 0;
}
