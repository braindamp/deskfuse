#include "render_core.h"
#include <array>
#include <cmath>
#include <chrono>
#include <cstdio>
#include <numbers>
#include <stdexcept>
using namespace df_audio;
static void require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}
static void publish(RenderCore& core, uint64_t source, int amplitude, bool concealed=false,
                    double frequency=0) {
    std::array<int16_t, kBlockFrames * kChannels> data{};
    for (uint32_t i=0;i<kBlockFrames;++i)
        for (uint32_t ch=0;ch<kChannels;++ch)
            data[i*kChannels+ch]=static_cast<int16_t>(std::lround(frequency ?
                amplitude*std::sin(2*std::numbers::pi*frequency*(source+i)/48000.0) : amplitude));
    require(core.publish(data.data(),kBlockFrames,{source,1,1,1,1,0,
                static_cast<uint8_t>(concealed)})==Admission::Accepted,"PCM admission");
}
static int qualify() {
    for(uint32_t reserve:{1440u,1200u,960u,720u,480u}) {
        RenderCore experiment(reserve);
        uint64_t source=0;
        std::array<int16_t,3840*kChannels> out{};
        while(source<960+reserve) {publish(experiment,source,1000);source+=240;}
        experiment.render(out.data(),960,{true,true});
        PlayoutState state{};
        require(experiment.playout_state(state)&&state.primed&&
            state.queued_playable_frames+96>=reserve,"selected reserve primes at same follower target");
        const auto rd=experiment.read(),wr=experiment.write();
        experiment.render(out.data(),3840,{true,true});
        require(experiment.unsupported_demand()==3840&&experiment.read()==rd&&experiment.write()==wr,
            "oversized demand fails even after priming without source release");
        experiment.render(out.data(),240,{true,true});
        require(experiment.unsupported_demand()==3840&&experiment.read()==rd,
            "unsupported demand remains terminal rather than silently reprime");
    }
    for(uint32_t demand:{48u,240u,480u,960u,1280u}) {
        RenderCore core;
        uint64_t source=0;
        for(uint32_t b=0;b<6;++b,source+=240)publish(core,source,1000);
        std::array<int16_t,1280*kChannels> out{};
        core.render(out.data(),demand,{false,true});
        PlayoutState state{};
        require(core.playout_state(state) && !state.primed &&
            core.legacy_rendered()==0 && core.legacy_underruns()==0,
            "1440 PREcallback frames do not establish POSTcallback target");
        while(source<demand+kTargetFrames) {
            publish(core,source,1000);
            source+=240;
        }
        core.render(out.data(),demand,{false,true});
        require(core.playout_state(state) && state.primed &&
            state.queued_playable_frames+StreamingSinc::future>=kTargetFrames &&
            core.legacy_underruns()==0,
            "first callback leaves controller source-inventory target");
        // Re-prime uses exactly the same condition and cannot resurrect the
        // smaller historical startup reserve after an authenticated reset.
        core.invalidate();
        core.render(out.data(),demand,{false,true});
        source+=240;
        const uint64_t anchor=source;
        for(uint32_t b=0;b<6;++b,source+=240)publish(core,source,1000);
        core.render(out.data(),demand,{false,true});
        require(core.playout_state(state) && !state.primed,
            "re-prime does not repeat the undersized initial reserve");
        while(source-anchor<demand+kTargetFrames) {
            publish(core,source,1000);
            source+=240;
        }
        core.render(out.data(),demand,{false,true});
        require(core.playout_state(state) && state.primed &&
            state.queued_playable_frames+StreamingSinc::future>=kTargetFrames &&
            core.legacy_underruns()==0,"re-prime matches output-demand target");
    }
    {   // A legacy backend can request a second 20ms chunk immediately.
        // The former 30ms PREcallback start left <10ms for that next pull.
        RenderCore core;
        for(uint64_t b=0;b<10;++b)publish(core,b*240,1000);
        std::array<int16_t,960*kChannels> out{};
        core.render(out.data(),960,{true,true});
        core.render(out.data(),960,{true,true});
        require(core.legacy_rendered()==1920 && core.legacy_silence()==0 &&
            core.legacy_underruns()==0,"two consecutive20ms output pulls survive startup");
    }
    {   // Unsupported demand cannot grow the ring or fake the target.
        RenderCore core;
        for(uint64_t b=0;b<16;++b)publish(core,b*240,1000);
        std::array<int16_t,2400*kChannels> out{};
        core.render(out.data(),2400,{false,true});
        PlayoutState state{};
        require(core.playout_state(state) && !state.primed && core.read()==0 &&
            core.write()==RenderCore::capacity,"impossible target remains bounded/unprimed");
    }
    StreamingSinc sinc;
    double max_passband_db=0, max_phase_error=0, max_stopband=0;
    for (uint32_t phase=0;phase<64;++phase) {
        const uint32_t fraction=phase*(uint32_t{1}<<26);
        const double f=static_cast<double>(fraction)/4294967296.0;
        for (double hz=100.0;hz<=20000.0;hz+=100.0) {
            const double omega=2*std::numbers::pi*hz/48000;
            const double re=sinc.sample(fraction,[&](int32_t i) {return static_cast<float>(std::cos(omega*i));});
            const double im=sinc.sample(fraction,[&](int32_t i) {return static_cast<float>(std::sin(omega*i));});
            const double gain_db=20*std::log10(std::hypot(re,im));
            max_passband_db=std::max(max_passband_db,std::abs(gain_db));
            max_phase_error=std::max(max_phase_error,std::abs(std::atan2(im,re)-omega*f));
        }
        for (double hz=23800.0;hz<=24000.0;hz+=5.0) {
            const double omega=2*std::numbers::pi*hz/48000;
            const double re=sinc.sample(fraction,[&](int32_t i) {return static_cast<float>(std::cos(omega*i));});
            const double im=sinc.sample(fraction,[&](int32_t i) {return static_cast<float>(std::sin(omega*i));});
            max_stopband=std::max(max_stopband,std::hypot(re,im));
        }
    }
    std::printf("SINC measured phases=64 passband100..20k/100Hz max_abs_db=%.6f phase_error_rad=%.7f stop23800..24000/5Hz_db=%.2f\n",
        max_passband_db,max_phase_error,20*std::log10(max_stopband));
    require(max_passband_db<0.05,"20kHz passband within0.05dB");
    require(max_phase_error<0.0002,"fractional delay phase accuracy");
    require(max_stopband<0.00001,"near-Nyquist rejection exceeds100dB");
    {   // The optimized LIVE stereo loop must match the qualified reference,
        // including both channels, ring wrap and explicitly padded startup.
        std::array<int16_t,kRingFrames*kChannels> pcm{};
        for(uint64_t i=0;i<kRingFrames;++i) {
            pcm[i*2]=static_cast<int16_t>((i*113)%32000);
            pcm[i*2+1]=static_cast<int16_t>(-static_cast<int32_t>((i*137)%32000));
        }
        for(uint64_t centre:{uint64_t{0},kRingFrames-1,kRingFrames*2+10})
            for(uint32_t phase=0;phase<16;++phase) {
                const uint32_t fraction=phase*(uint32_t{1}<<28);
                const auto fast=sinc.sample_stereo(fraction,pcm.data(),kRingFrames,centre,0);
                for(uint32_t ch=0;ch<kChannels;++ch) {
                    const float reference=sinc.sample(fraction,[&](int32_t offset) {
                        if(offset<0 && centre<static_cast<uint32_t>(-offset))return 0.0f;
                        const uint64_t at=offset<0?centre-static_cast<uint32_t>(-offset):
                            centre+static_cast<uint32_t>(offset);
                        return static_cast<float>(pcm[(at%kRingFrames)*2+ch]);
                    });
                    require(std::abs(reference-fast[ch])<0.01f,"live stereo/ring-wrap filter matches reference");
                }
            }
    }
    // End-to-end variable-phase and ratio-ramp residual (no i16 rounding).
    for (int ppm : {-400,100,400}) {
        double phase=64.0, sum_error=0, sum_signal=0;
        for (uint32_t i=0;i<48000;++i) {
            const auto centre=static_cast<int64_t>(phase);
            const auto fraction=static_cast<uint32_t>((phase-centre)*4294967296.0);
            constexpr double omega=2*std::numbers::pi*20000.0/48000.0;
            const double value=sinc.sample(fraction,[&](int32_t offset) {
                return static_cast<float>(std::sin(omega*(centre+offset)));
            });
            const double expected=std::sin(omega*phase);
            sum_error+=(value-expected)*(value-expected);
            sum_signal+=expected*expected;
            const double ramp_ppm=ppm*static_cast<double>(i)/48000;
            phase+=1.0+ramp_ppm/1e6;
        }
        const double residual_db=10*std::log10(sum_error/sum_signal);
        std::printf("SINC20k dynamic ratio0..%dppm residual_db=%.2f\n",ppm,residual_db);
        require(residual_db < -65,"dynamic ASRC residual below-65dB");
    }
    // Exact production RenderCore, including support tags and release cursor.
    for (uint32_t count : {48u,96u,192u,240u,480u,960u}) {
        RenderCore core;
        for (uint64_t b=0;b<12;++b) publish(core,b*240,10000,false,20000.0);
        std::array<int16_t,960*kChannels> out{};
        core.render(out.data(),96,{false,true}); // explicit startup padding excluded
        CoreSnapshot record{};
        require(core.pop_snapshot(record),"startup callback record");
        core.render(out.data(),count,{false,true});
        require(core.pop_snapshot(record) && record.rendered==count+96,"actual-core callback demand");
        const double exact_phase=static_cast<double>(record.last_source)+
            static_cast<double>(record.fraction_q32)/4294967296.0;
        const double expected=10000*std::sin(2*std::numbers::pi*20000.0*exact_phase/48000.0);
        require(std::abs(out[(count-1)*kChannels]-expected)<4,"actual-core20k source-phase alignment");
        require(record.last_support_source==record.last_source+StreamingSinc::future,
            "explicit finite future support");
        PlayoutState demand{};
        require(core.playout_state(demand) && demand.primed &&
            demand.max_callback_frames==std::max(96u,count),"authoritative callback demand");
        const uint64_t cursor=record.last_source+1;
        require(core.read()+StreamingSinc::past>=cursor-1 &&
            core.read()+StreamingSinc::past<=cursor+1,"release retains required past support");
        require(demand.queued_playable_frames<=12*240-96-count &&
            demand.queued_playable_frames>=12*240-96-count-StreamingSinc::future,
            "inventory excludes held past and unavailable future support");
    }
    {   // Reconstruction overshoot is explicitly saturated AND counted.
        // No hidden global gain: ordinary full-scale DC keeps its sign/value.
        RenderCore core;
        for(uint64_t b=0;b<12;++b)publish(core,b*240,b<4?32767:-32768);
        std::array<int16_t,1440*kChannels> out{};
        core.render(out.data(),1440,{false,true});
        PlayoutState state{};
        require(core.playout_state(state) && state.clipped_samples>0,
            "full-scale reconstruction saturation is reported");
        for(uint32_t i=300;i<800;++i)
            require(out[i*kChannels]>=32765,"positive full-scale cannot wrap negative");
        for(uint32_t i=1150;i<1400;++i)
            require(out[i*kChannels]<=-32766,"negative full-scale cannot wrap positive");
        std::printf("ACTUAL_CORE full-scale step clipped_channel_samples=%llu\n",
            static_cast<unsigned long long>(state.clipped_samples));
    }
    // Concealment entry/exit occurs across callbacks, with both signs and
    // drift enabled. No underrun: these are genuinely missing source frames.
    for (int amplitude : {-12000,12000}) {
        RenderCore core;
        for (uint64_t b=0;b<12;++b) publish(core,b*240,b==2?0:amplitude,b==2);
        std::array<int16_t,96*kChannels> out{};
        int previous=0, max_jump=0;
        for (uint32_t callback=0;callback<10;++callback) {
            core.render(out.data(),96,{true,true});
            for(uint32_t i=0;i<96;++i) {
                const int sample=out[i*kChannels];
                max_jump=std::max(max_jump,std::abs(sample-previous));
                previous=sample;
            }
        }
        CoreSnapshot value{},last{};
        while(core.pop_snapshot(value))last=value;
        require(last.underruns==0 && last.concealed_outputs>=240,"loss remains explicitly accounted");
        require(max_jump<2000,"bounded conceal transitions replace full-scale discontinuity");
        std::printf("ACTUAL_CORE conceal amplitude=%d max_single_frame_jump=%d\n",amplitude,max_jump);
    }
    {   // Informational HOST execution cost; neither an Android WCET bound
        // nor a physical-device acceptance gate.
        RenderCore core;
        uint64_t source=0;
        for(uint32_t b=0;b<8;++b,source+=240)publish(core,source,1000);
        std::array<int16_t,192*kChannels> out{};
        std::array<int64_t,2000> times{};
        for(auto& elapsed:times) {
            const auto start=std::chrono::steady_clock::now();
            core.render(out.data(),192,{true,true});
            elapsed=std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now()-start).count();
            while(core.write()-core.read()<1920) {
                publish(core,source,1000);
                source+=240;
            }
        }
        std::sort(times.begin(),times.end());
        double total_ns=0;
        for(const auto elapsed:times)total_ns+=static_cast<double>(elapsed);
        std::printf("HOST_ONLY 192frame(4000us)callback mean_us=%.1f p50_us=%.1f p99_us=%.1f max_observed_us=%.1f\n",
            total_ns/static_cast<double>(times.size())/1000.0,times[999]/1000.0,
            times[1979]/1000.0,times.back()/1000.0);
        PlayoutState state{};
        require(core.playout_state(state) && state.primed,"sustained host callback execution");
        require(core.legacy_underruns()==0,"host benchmark must not hide underruns");
    }
    std::puts("PASS live finite-support ASRC, callback phase/release, bounded concealment transitions");
    return 0;
}
int main() {
    try { return qualify(); }
    catch (const std::exception& e) { std::fprintf(stderr,"FAIL: %s\n",e.what()); return 1; }
}
