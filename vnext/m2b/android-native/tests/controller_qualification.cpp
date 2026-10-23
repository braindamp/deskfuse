#include "clock_qualification.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <stdexcept>
#include <vector>
using namespace df_audio;
void require(bool ok, const char* what) { if (!ok) throw std::runtime_error(what); }
struct Sim {
    TimeBasedFollower follower{};
    double occupancy = 1440.0, min_occ = 1440.0, max_occ = 1440.0;
    double prev_ppm = 0;
    int underruns = 0, overflows = 0;
    void sample(uint32_t n, double source_ppm, double delivery_delta = 0) {
        const double dt = n / 48000.0;
        // Available occupancy BEFORE callback frames are consumed.
        occupancy += n * (1.0 + source_ppm / 1e6) + delivery_delta;
        const double ppm = follower.update(
            n, static_cast<int64_t>(std::llround(occupancy)), 1440);
        require(std::abs(ppm) <= 400.000001, "correction cap");
        require(std::abs(ppm - prev_ppm) <= 100 * dt + 1e-5, "slew cap");
        occupancy -= n * (1.0 + ppm / 1e6);
        min_occ = std::min(min_occ, occupancy);
        max_occ = std::max(max_occ, occupancy);
        if (occupancy < 0) { ++underruns; occupancy = 0; follower.reset(); }
        if (occupancy > 3840) { ++overflows; occupancy = 3840; follower.reset(); }
        prev_ppm = (underruns || overflows) ? follower.ppm() : ppm;
    }
    void run(uint32_t n, int seconds, double drift) {
        for (int i = 0; i < seconds * 48000 / static_cast<int>(n); ++i)
            sample(n, drift);
    }
};
int main() {
    std::vector<double> final_occ;
    for (int n : {48, 96, 192, 240, 480, 960}) {
        for (int drift : {-500, -100, 0, 100, 500}) {
            Sim s;
            s.run(n, 90, drift);
            if (std::abs(drift) <= 100) {
                require(!s.underruns && !s.overflows, "in-range drift does not starve");
                require(s.min_occ > 1100 && s.max_occ < 1800, "bounded in-range occupancy");
            }
            if (std::abs(drift) == 500)
                require(s.follower.ppm() * drift > 0 &&
                        std::abs(s.follower.ppm()) > 390,
                        "uncontrollable 500 ppm must saturate");
            if (drift == 100) final_occ.push_back(s.occupancy);
            std::printf("frames=%d drift=%d correction=%.2f occupancy=%.2f\n",
                        n, drift, s.follower.ppm(), s.occupancy);
        }
    }
    const auto [min, max] = std::minmax_element(final_occ.begin(), final_occ.end());
    require(*max - *min < 4.0, "callback-size invariance");
    Sim jitter;
    jitter.run(240, 5, 100);
    for (int i = 0; i < 12000; ++i)
        jitter.sample(240, 100, i % 317 == 5 ? 240 :
                    (i % 317 == 6 ? -240 : 0));
    require(jitter.follower.disturbances() > 0, "jitter disturbance gating");
    require(!jitter.underruns && !jitter.overflows, "arrival bursts stay bounded");
    Sim loss;
    loss.run(192, 5, 100);
    for (int i = 0; i < 10000; ++i)
        loss.sample(192, 100, (i == 300 || i == 301) ? -240 : 0);
    require(loss.follower.disturbances() > 0, "loss disturbance gating");
    require(!loss.underruns && !loss.overflows, "short packet loss stays bounded");
    Sim recovery;
    recovery.run(240, 10, 100);
    for (int i = 0; i < 40; ++i) recovery.sample(240, 100, -240);
    require(recovery.underruns > 0, "prolonged outage must expose underrun");
    recovery.follower.reset();
    recovery.occupancy = 1440;
    recovery.prev_ppm = 0;
    recovery.run(240, 30, 100);
    require(recovery.occupancy > 1000 && recovery.occupancy < 1800,
            "explicit re-prime recovery");
    // +/-500ppm lies OUTSIDE the +/-400ppm actuator authority.
    // Long-duration occupation must eventually hit the fixed-capacity
    // recovery boundary, not be reported as indefinitely stable correction.
    Sim too_fast, too_slow;
    too_fast.run(240, 900, 500);
    too_slow.run(240, 900, -500);
    require(too_fast.overflows > 0,
            "+500ppm must eventually expose unrecoverable ring pressure");
    require(too_slow.underruns > 0,
            "-500ppm must eventually expose unavoidable output underrun");
    // Explicit recovery: discard obsolete samples, re-prime the existing
    // 30ms target, and ensure an in-authority source again remains bounded.
    too_fast.follower.reset();
    too_fast.occupancy = 1440;
    too_fast.prev_ppm = 0;
    too_fast.run(240, 45, 100);
    require(too_fast.occupancy > 900 && too_fast.occupancy < 2000,
            "recover from over-authority positive drift via fresh re-prime");

    Sim mixed;
    for (int i = 0; i < 40000; ++i) {
        constexpr int sizes[] = {48, 96, 192, 240, 480, 960};
        const int n = sizes[i % 6];
        mixed.sample(n, i % 5000 < 2500 ? 100 : -100,
                     i % 1207 == 0 ? 240 : (i % 1207 == 1 ? -240 : 0));
    }
    require(mixed.follower.disturbances() > 0, "mixed-size delivery jitter");
    require(!mixed.underruns && !mixed.overflows, "mixed callback load bounded");
    std::puts("PASS CP-001 deterministic clock envelope: callback sizes, +/-100/500 ppm, bursts, loss, underrun and recovery");
}
