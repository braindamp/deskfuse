#include "libsamplerate_candidate.h"
#include <array>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <numbers>
#include <stdexcept>
#include <vector>

using namespace df_audio;
static void require(bool b,const char* msg){if(!b)throw std::runtime_error(msg);}
static constexpr double fs=48000.0;
static constexpr SrcTimeline timeline{7,4,1,2,3};
struct Measure {
    double db=0, max_absolute=0, right_difference=0;
    uint64_t output=0, released_to_library=0, phase_estimate=0;
    double avg_us_per_240=0;
};
// Real persistent streaming src_new/src_process lifecycle, 240-frame blocks,
// interleaved stereo and no per-block SRC_STATE reset. The first transient is
// excluded from steady-state spectra; impulse and restarts have own checks.
static Measure run(double frequency,double drift_ppm,int blocks,bool transient=false) {
    PersistentSrcCandidate src(SRC_SINC_MEDIUM_QUALITY);
    require(src.ready(),"libsamplerate SINC_MEDIUM_QUALITY available");
    require(src.reset(timeline,48000),"fresh authenticated converter context");
    std::array<float,480> in{};
    std::array<float,1920> out{};
    double sum_sq=0;
    uint64_t counted=0;
    Measure m{};
    const auto t0=std::chrono::steady_clock::now();
    for(int block=0;block<blocks;++block) {
        for(int frame=0;frame<240;++frame) {
            const int i=block*240+frame;
            const double v= transient
                ? (i==480 ? 1.0 : (i==960 ? -1.0 : 0.0))
                : 0.5*std::sin(2.0*std::numbers::pi*frequency*i/fs);
            in[frame*2]=static_cast<float>(v);
            in[frame*2+1]=static_cast<float>(v);
        }
        require(src.submit(in.data(),240,48000+uint64_t(block)*240,timeline)==
                SrcResult::Ok,"continuous source frames submitted");
        // One active correction authority. Library ratio is inverse of
        // desired source-input consumption rate; step smoothly +/-400ppm.
        const double ratio=1.0/(1.0+drift_ppm/1e6);
        uint32_t produced=0;
        require(src.process(out.data(),960,ratio,produced)==SrcResult::Ok,
                "persistent libsamplerate processing");
        for(unsigned i=0;i<produced;++i){
            const double left=out[i*2],right=out[i*2+1];
            require(std::isfinite(left)&&std::isfinite(right),"finite output");
            m.right_difference=std::max(m.right_difference,std::abs(left-right));
            m.max_absolute=std::max(m.max_absolute,std::abs(left));
            if(block>=blocks/2 && !transient){
                sum_sq+=left*left;++counted;
            }
        }
        m.output+=produced;
        require(src.report().input_fifo_frames<=PersistentSrcCandidate::input_capacity,
                "bounded one-receiver input FIFO");
    }
    const auto elapsed=std::chrono::steady_clock::now()-t0;
    m.avg_us_per_240=std::chrono::duration<double,std::micro>(elapsed).count()/blocks;
    m.released_to_library=src.report().input_released_to_library;
    m.phase_estimate=src.report().estimated_phase_q32;
    if(!transient){
        require(counted>3000,"spectral acquisition window long enough");
        const double rms=std::sqrt(sum_sq/counted);
        m.db=20.0*std::log10(rms/(0.5/std::sqrt(2.0)));
    }
    return m;
}
int main(){
    for(const double frequency:{1000.0,10000.0,20000.0}){
        const auto m=run(frequency,0.0,300);
        std::printf("ASRC_MEDIUM f=%gHz gain=%.3fdB outputs=%llu input_released=%llu peak=%.3f avg_us_5ms=%.1f\n",
                    frequency,m.db,
                    static_cast<unsigned long long>(m.output),
                    static_cast<unsigned long long>(m.released_to_library),
                    m.max_absolute,m.avg_us_per_240);
        require(m.right_difference<0.000001,
                "identical stereo channels remain aligned");
        require(m.avg_us_per_240<5000.0,
                "sustained processing average exceeds 5ms block budget");
        require(m.output>50000 && m.output<72001,"persistent output within expected range");
        if(frequency==1000)require(std::abs(m.db)<0.3,"1kHz spectral reference");
        if(frequency==10000)require(m.db>-1.0,"10kHz HF fidelity");
        if(frequency==20000)require(m.db>-3.0,
                "20kHz band drop worse than provisional candidate budget");
    }
    for(double drift:{-400.0,100.0,400.0}){
        auto m=run(1000.0,drift,300);
        require(m.output>50000 && m.output<72001,"bounded time-varying ASRC output");
        require(m.right_difference<1e-6,"stereo drift alignment");
        require(m.avg_us_per_240<5000,"ratio-change CPU budget");
    }
    auto impulse=run(0,0,100,true);
    std::printf("ASRC_MEDIUM impulse/max full_scale=%.4f avg_us_5ms=%.1f\n",
                impulse.max_absolute,impulse.avg_us_per_240);
    require(impulse.max_absolute>0.5,"impulse reaches output");
    require(impulse.max_absolute<1.5,"full-scale transient cannot exceed 1.5 float bound");
    // Do NOT confuse library input_frames_used with output presentation phase.
    PersistentSrcCandidate x;
    require(x.ready() && x.reset(timeline,100000),"state reset");
    const auto block=[] {
        std::array<float,480> samples{};
        samples.fill(0.1f);
        return samples;
    }();
    require(x.submit(block.data(),240,100000,timeline)==SrcResult::Ok,"one continuous chunk");
    require(x.submit(block.data(),240,100240,timeline)==SrcResult::Ok,"second continuous chunk");
    require(x.submit(block.data(),240,100300,timeline)==SrcResult::Gap,
            "reject overlapping source interval");
    require(x.submit(block.data(),240,100700,SrcTimeline{8,4,1,2,3})==SrcResult::Gap,
            "reject epoch restart with stored old filter support");
    require(x.report().discontinuities==2,"explicitly count rejected discontinuities");
    require(x.reset(SrcTimeline{8,4,1,2,4},100700),"re-prime separated filter generation");
    require(x.submit(block.data(),240,100700,SrcTimeline{8,4,1,2,4})==SrcResult::Ok,
            "new source incarnation admitted after reset");
    std::puts("PASS libsamplerate persistent SINC_MEDIUM first-candidate spectral, stereo, CPU and source/phase separation; NOT production selection");
}
