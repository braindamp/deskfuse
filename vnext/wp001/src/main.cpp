#include "deskfuse/timing_lab.hpp"
#include <cstdlib>
#include <iostream>
#include <string_view>

int main(int argc, char** argv) {
    using namespace df;
    if (argc > 1 && std::string_view(argv[1]) == "--help") {
        std::cout << "df_timing_lab [normal|burst100|stall100|idle_reset]\n"
            "Standalone deterministic 48 kHz PCM experiment. No real sockets or drivers.\n";
        return 0;
    }
    const auto scenario = (argc > 1) ? std::string_view(argv[1]) : "normal";
    TimingLab lab;
    if (scenario == "normal") {
        for (std::uint64_t i = 0; i < 10; ++i) {
            lab.capture({i * 480, 480}, (i + 1) * 10'000'000ULL);
            lab.service((i + 1) * 10'000'000ULL);
            lab.commit_receiver((i + 1) * 480, StrictSink::Mode::StrictStop);
        }
    } else if (scenario == "burst100") {
        lab.capture({0, 4'800}, 100'000'000ULL);
        lab.service(100'000'000ULL);
        lab.commit_receiver(4'800, StrictSink::Mode::AccountMissing);
    } else if (scenario == "stall100") {
        for (std::uint64_t i = 0; i < 10; ++i)
            lab.capture({i * 480, 480}, (i + 1) * 10'000'000ULL);
        lab.service(100'000'000ULL);
        lab.commit_receiver(4'800, StrictSink::Mode::AccountMissing);
    } else if (scenario == "idle_reset") {
        lab.capture({0, 480}, 10'000'000ULL);
        lab.service(10'000'000ULL);
        lab.commit_receiver(480, StrictSink::Mode::StrictStop);
        lab.service(510'000'000ULL); // 500ms without capture is not a source backlog.
        lab.reset({2, 2, 2}, 0, 500'000'000ULL);
        lab.capture({0, 480}, 510'000'000ULL);
        lab.service(510'000'000ULL);
        lab.commit_receiver(480, StrictSink::Mode::StrictStop);
    } else {
        std::cerr << "Unknown scenario: " << scenario << "\n";
        return 2;
    }
    const auto& s = lab.stats();
    const auto& r = lab.sink().stats();
    std::cout << "scenario=" << scenario << " source_frames=" << s.source_frames
              << " precopy_expired=" << s.precopy_expired_frames
              << " ring_full=" << s.ring_full_frames
              << " transport_expired=" << s.transport_expired_frames
              << " offered=" << s.offered_frames
              << " received=" << s.received_frames
              << " committed_real=" << r.real_frames
              << " committed_missing=" << r.missing_frames
              << " rejected=" << r.rejected
              << " blocks_peak=" << s.high_water_blocks
              << " trace_overflow=" << lab.trace().overrun() << "\n";
    return (r.corruption == 0 && lab.trace().overrun() == 0) ? EXIT_SUCCESS : EXIT_FAILURE;
}
