#pragma once
// MP-001 first persistent, host-qualified streaming ASRC comparator.
// Not selected as an Android callback renderer until filter-support identity,
// allocation and phase calibration qualify. One instance, one processing owner.
// All scratch memory is bounded and supplied by the caller; src_new/reset/delete
// belong to lifecycle/control, NEVER a real-time AAudio callback.
#include <samplerate.h>
#include <array>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>

namespace df_audio {
struct SrcTimeline {
    uint32_t epoch=0, stream=0, format=0, connection=0, playout=0;
    [[nodiscard]] bool operator==(const SrcTimeline&) const = default;
};
struct SrcReport {
    uint64_t input_admitted=0;
    uint64_t input_released_to_library=0; // NOT exact presentation phase
    uint64_t output_generated=0;
    uint64_t discontinuities=0;
    uint64_t resets=0;
    uint64_t estimated_phase_q32=0; // diagnostic only, NOT qualified for media identity
    uint64_t library_buffered_support_unknown=1;
    uint32_t input_fifo_frames=0;
};
enum class SrcResult : int {Ok=0, Full=1, Gap=2, BadArgument=-1, LibraryError=-2};
class PersistentSrcCandidate {
public:
    static constexpr uint32_t channels=2, chunk_frames=240, input_capacity=3840;
    static constexpr uint32_t output_capacity=960;
    explicit PersistentSrcCandidate(int quality=SRC_SINC_MEDIUM_QUALITY) noexcept {
        int error=0;
        state_=src_new(quality,channels,&error);
        init_error_=error;
    }
    ~PersistentSrcCandidate() { if(state_) state_=src_delete(state_); }
    PersistentSrcCandidate(const PersistentSrcCandidate&)=delete;
    PersistentSrcCandidate& operator=(const PersistentSrcCandidate&)=delete;
    bool ready() const noexcept {return state_!=nullptr;}
    int error() const noexcept {return init_error_;}

    // Lifecycle-owned; reset discards old filter state and establishes a fresh
    // authenticated first source frame. Forbidden on the output callback.
    bool reset(SrcTimeline timeline,uint64_t source_anchor) noexcept {
        if(!state_ || src_reset(state_)!=0)return false;
        timeline_=timeline;
        next_source_=source_anchor;
        frames_buffered_=0;
        report_.resets++;
        report_.input_fifo_frames=0;
        // IMPORTANT: libsamplerate's FIR filter delay is NOT represented by
        // input_frames_used. This independent nominal phase estimate remains
        // unqualified until a latency/filter-support calibration is measured.
        nominal_phase_ = static_cast<long double>(source_anchor);
        anchored_=true;
        return true;
    }
    SrcResult submit(const float* interleaved,uint32_t frames,
                     uint64_t first_source,SrcTimeline timeline) noexcept {
        if(!state_ || !interleaved || !anchored_ ||
           frames==0 || frames>chunk_frames ||
           first_source>std::numeric_limits<uint64_t>::max()-frames)
            return SrcResult::BadArgument;
        if(!(timeline==timeline_) || first_source!=next_source_) {
            report_.discontinuities++;
            return SrcResult::Gap; // never leak sinc support across timelines
        }
        if(input_capacity-frames_buffered_<frames) return SrcResult::Full;
        std::memcpy(input_.data()+frames_buffered_*channels,interleaved,
                    static_cast<size_t>(frames)*channels*sizeof(float));
        frames_buffered_+=frames;
        next_source_+=frames;
        report_.input_admitted+=frames;
        report_.input_fifo_frames=frames_buffered_;
        return SrcResult::Ok;
    }
    // Processing owner only. Intended for offline qualification, NOT the
    // callback until zero-allocation and support lifetime are proven.
    SrcResult process(float* out,uint32_t max_output,double src_ratio,
                      uint32_t& generated) noexcept {
        generated=0;
        if(!state_||!out || max_output==0 || max_output>output_capacity ||
           !std::isfinite(src_ratio) || !(src_ratio>0.0) ||
           std::abs(src_ratio-1.0)>0.002)return SrcResult::BadArgument;
        if(frames_buffered_==0)return SrcResult::Ok;
        SRC_DATA d{};
        d.data_in=input_.data();
        d.input_frames=static_cast<long>(frames_buffered_);
        d.data_out=out;
        d.output_frames=static_cast<long>(max_output);
        d.src_ratio=src_ratio;
        d.end_of_input=0;
        int err=src_process(state_,&d);
        if(err || d.input_frames_used<0 || d.output_frames_gen<0 ||
           static_cast<unsigned long>(d.input_frames_used)>frames_buffered_ ||
           static_cast<unsigned long>(d.output_frames_gen)>max_output) {
            last_error_=err;
            return SrcResult::LibraryError;
        }
        const auto consumed=static_cast<uint32_t>(d.input_frames_used);
        generated=static_cast<uint32_t>(d.output_frames_gen);
        if(consumed && consumed<frames_buffered_) {
            std::memmove(input_.data(),input_.data()+consumed*channels,
                static_cast<size_t>(frames_buffered_-consumed)*channels*sizeof(float));
        }
        frames_buffered_-=consumed;
        report_.input_fifo_frames=frames_buffered_;
        report_.input_released_to_library+=consumed;
        report_.output_generated+=generated;
        // A nominal theoretical source advance, distinct from actual FIR
        // support and from library input_frames_used. During ratio ramps the
        // exact phase is unknown: do NOT publish as authoritative source ID.
        nominal_phase_ += static_cast<long double>(generated)/src_ratio;
        const long double q32=nominal_phase_*4294967296.0L;
        if(q32>=0.0L && q32<static_cast<long double>(UINT64_MAX))
            report_.estimated_phase_q32=static_cast<uint64_t>(q32);
        return SrcResult::Ok;
    }
    const SrcReport& report()const noexcept{return report_;}
    int library_error()const noexcept{return last_error_;}
private:
    SRC_STATE* state_=nullptr;
    int init_error_=0, last_error_=0;
    std::array<float,input_capacity*channels> input_{};
    uint32_t frames_buffered_=0;
    bool anchored_=false;
    uint64_t next_source_=0;
    SrcTimeline timeline_{};
    SrcReport report_{};
    long double nominal_phase_=0;
};
} // namespace df_audio
