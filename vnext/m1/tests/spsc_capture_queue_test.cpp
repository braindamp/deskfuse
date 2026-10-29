// Host-only test of the exact queue used by event-driven Windows capture.
// No WASAPI device, audio endpoint, QUIC, driver, phone or network required.
#include "../src/spsc_capture_queue.h"

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <thread>

struct Packet {
    uint64_t frame = 0;
    std::array<uint8_t, 960> pcm{};
};

using Queue = df_wasapi::SpscCaptureQueue<Packet, 8>;

int main() {
    // Reserve/publish is invisible until committed; a borrow pins its slot
    // across producer wraparound, and only its exact token releases storage.
    {
        Queue q;
        Packet* slot=nullptr; uint64_t token=0;
        if (!q.try_reserve(slot,token) || !slot || !token) return 20;
        slot->frame=42; slot->pcm.fill(77);
        const Packet* borrowed=nullptr; uint64_t consumer=0;
        if (q.try_borrow(borrowed,consumer) || q.publish(token+1)) return 21;
        if (!q.publish(token) || q.publish(token)) return 22;
        if (!q.try_borrow(borrowed,consumer) || !q.has_borrow() || borrowed->frame!=42) return 23;
        for (int n=0;n<7;++n) {Packet p{}; if (!q.try_push(p)) return 24;}
        Packet* unavailable=reinterpret_cast<Packet*>(1); uint64_t none=1;
        if (q.try_reserve(unavailable,none) || unavailable || none || q.dropped()!=1) return 25;
        const Packet* second=nullptr; uint64_t second_token=0;
        if (q.try_borrow(second,second_token) || q.consume(consumer+1) || borrowed->pcm[959]!=77) return 26;
        q.finish(); // join/terminal does not release an outstanding consumer borrow
        if (!q.has_borrow() || !q.consume(consumer) || q.consume(consumer)) return 27;
        for (int n=0;n<7;++n) {Packet p{}; if (!q.pop(p)) return 28;}
        Packet p{}; if (q.pop(p) || q.has_records()) return 29;
    }
    // Full means bounded producer drop, not a block or overwritten PCM.
    {
        Queue q;
        for (uint64_t n = 0; n < 8; ++n) {
            Packet p{};
            p.frame = n * 240;
            p.pcm[0] = static_cast<uint8_t>(n);
            if (!q.try_push(p)) return 1;
        }
        Packet extra{};
        extra.frame = 8 * 240;
        if (q.try_push(extra) || q.dropped() != 1) return 2;
        Packet read{};
        if (!q.pop(read) || read.frame != 0 || read.pcm[0] != 0) return 3;
        if (!q.try_push(extra)) return 4;
        q.finish();
        for (uint64_t n = 1; n <= 8; ++n) {
            if (!q.pop(read) || read.frame != n * 240 ||
                read.pcm[0] != static_cast<uint8_t>(n == 8 ? 0 : n)) return 5;
        }
        if (q.pop(read)) return 6;
        if (q.try_push(extra) || q.dropped() != 2) return 7;
    }

    // Writer sleeping BEFORE first publication must wake correctly.
    {
        Queue q;
        std::atomic<uint64_t> observed{UINT64_MAX};
        std::thread writer([&] {
            Packet p{};
            if (q.pop(p)) observed.store(p.frame);
        });
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
        Packet p{};
        p.frame = 12345;
        if (!q.try_push(p)) { q.finish(); writer.join(); return 8; }
        q.finish();
        writer.join();
        if (observed.load() != 12345) return 9;
    }

    // Force the shutdown race: consumer observes empty, pauses before its
    // acquire-close read; producer publishes the final record and closes.
    // An old one-shot close check would lose that accepted final record.
    {
        Queue q;
        struct Pause {
            HANDLE observed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            HANDLE resume = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            std::atomic<bool> first{true};
        } pause;
        if (!pause.observed || !pause.resume) return 11;
        q.set_empty_probe([](void* p) {
            auto& state = *static_cast<Pause*>(p);
            if (!state.first.exchange(false)) return;
            SetEvent(state.observed);
            if (WaitForSingleObject(state.resume, 5000) != WAIT_OBJECT_0)
                std::terminate();
        }, &pause);
        std::atomic<uint64_t> received{UINT64_MAX};
        std::thread writer([&] {
            Packet p{};
            if (q.pop(p)) received.store(p.frame);
        });
        if (WaitForSingleObject(pause.observed, 5000) != WAIT_OBJECT_0) {
            SetEvent(pause.resume);
            q.finish();
            writer.join();
            CloseHandle(pause.observed);
            CloseHandle(pause.resume);
            return 12;
        }
        Packet last{};
        last.frame = 54321;
        const bool accepted = q.try_push(last);
        q.finish();
        SetEvent(pause.resume);
        writer.join();
        CloseHandle(pause.observed);
        CloseHandle(pause.resume);
        if (!accepted || received.load() != 54321 || q.dropped() != 0) return 13;
        Packet empty{};
        if (q.pop(empty)) return 14;
    }

    // Concurrent wraparound: accepted data remains ordered and unchanged,
    // even while the producer drops entire packets during writer stalls.
    {
        Queue q;
        std::atomic<bool> valid{true};
        std::atomic<uint64_t> received{0};
        std::thread writer([&] {
            Packet p{};
            uint64_t previous = 0;
            bool first = true;
            while (q.pop(p)) {
                if ((!first && p.frame <= previous) ||
                    p.pcm[0] != static_cast<uint8_t>(p.frame & 0xff) ||
                    p.pcm[959] != static_cast<uint8_t>((p.frame >> 8) & 0xff)) {
                    valid.store(false);
                }
                first = false;
                previous = p.frame;
                received.fetch_add(1);
            }
        });
        uint64_t accepted = 0;
        constexpr uint64_t count = 200000;
        for (uint64_t n = 0; n < count; ++n) {
            Packet p{};
            p.frame = n;
            p.pcm[0] = static_cast<uint8_t>(n & 0xff);
            p.pcm[959] = static_cast<uint8_t>((n >> 8) & 0xff);
            if (q.try_push(p)) ++accepted;
        }
        q.finish();
        writer.join();
        if (!valid.load() || received.load() != accepted ||
            accepted + q.dropped() != count) return 10;
    }
    std::cout << "PASS: nonblocking capture, FIFO, exact PCM, bounded drops, final-block shutdown race, idle wake\n";
    return 0;
}
