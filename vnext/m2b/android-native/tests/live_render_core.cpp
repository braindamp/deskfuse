#include "render_core.h"
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <stdexcept>
#include <thread>
#include <vector>
using namespace df_audio;
static void expect(bool condition, const char* reason) {
    if (!condition) throw std::runtime_error(reason);
}
static std::array<int16_t, kBlockFrames * kChannels> block(int value) {
    std::array<int16_t, kBlockFrames * kChannels> pcm{};
    for (auto& sample : pcm) sample = static_cast<int16_t>(value);
    return pcm;
}
static MediaIdentity tag(uint64_t first, uint32_t epoch=1,
                         uint32_t stream=7, uint32_t format=1,
                         uint32_t connection=11, bool concealed=false) {
    return {first, epoch, stream, format, connection, 0,
            static_cast<uint8_t>(concealed)};
}
static void enqueue(RenderCore& core, uint64_t source, int value,
                    bool concealed=false) {
    const auto pcm = block(value);
    expect(core.publish(pcm.data(), kBlockFrames,
                        tag(source,1,7,1,11,concealed)) ==
           Admission::Accepted, "valid packet accepted");
}
static void prime(RenderCore& core, uint64_t start=48000) {
    for (int i=0;i<8;++i) enqueue(core, start + i*240, 100+i);
}
int main() {
    {   // Same actual RenderCore used by the AAudio callback, not a model.
        RenderCore core;
        prime(core);
        std::array<int16_t, 600*2> out{};
        core.render(out.data(), 600, {false,false});
        for (int i=0;i<600;++i)
            expect(out[i*2] == 100+i/240, "continuous PCM packet boundary");
        CoreSnapshot s{};
        expect(core.pop_snapshot(s), "coherent output callback record");
        expect(s.rendered == 600 && s.released == 600 &&
               s.last_source == 48599 && s.fraction_q32 == 0,
               "commit/release input and exact output source phase");
    }
    {   // No fractional output may combine a captured source frame
        // with an incompatible gap/epoch/format/connection lookahead.
        // This directly invokes the production callback guard with a
        // host-only corrupt-tag seam; normal publish never creates it.
        for (int mode=0;mode<4;++mode) {
            RenderCore core;
            prime(core,0);
            auto wrong=tag(1);
            if (mode==0) wrong.source_frame=99; // missing interval
            if (mode==1) wrong.epoch=2;
            if (mode==2) wrong.format=2;
            if (mode==3) wrong.connection=12;
            core.test_set_tag(1,wrong);
            std::array<int16_t,96> out{};
            core.render(out.data(),48,{false,true});
            CoreSnapshot rec{};
            expect(core.pop_snapshot(rec),"discontinuity must publish record");
            expect(rec.rendered==0 && rec.silence==48,
                   "bad support pair never contributes to output");
            expect(core.requested_recoveries()>core.applied_recoveries(),
                   "incompatible interpolation support forces callback recovery");
            core.render(out.data(),48,{false,true});
            expect(core.applied_recoveries()==core.requested_recoveries(),
                   "callback exclusively applies source-generation invalidation");
        }
    }
    {   // Both interpolation taps must be contiguous within one identity.
        RenderCore core;
        prime(core);
        std::array<int16_t, 480*2> out{};
        core.render(out.data(), 480, {false,true});
        CoreSnapshot s{};
        expect(core.pop_snapshot(s) && s.rendered == 480,
               "valid consecutive packet interpolation");
        auto next = block(900);
        expect(core.publish(next.data(), 240, tag(50500)) ==
               Admission::Discontinuity, "gap requests explicit re-prime");
        const uint64_t ticket = core.requested_recoveries();
        expect(ticket != core.applied_recoveries(), "recovery is latched");
        core.render(out.data(), 120, {true,true});
        expect(core.applied_recoveries() == ticket,
               "only callback processes critical invalidation");
        expect(core.publish(next.data(),240,tag(48100)) == Admission::Stale,
               "late packet cannot move irreversible source frontier back");
        expect(core.publish(next.data(),240,tag(50500)) == Admission::Accepted,
               "new forward source anchor after recovery");
    }
    {   // Tags for short concealment are publication-verified, and any
        // fractional interpolation into a concealed support counts.
        RenderCore core;
        for (int i=0;i<3;++i) enqueue(core,i*240,100);
        enqueue(core,720,0,true);
        for (int i=4;i<9;++i) enqueue(core,i*240,100);
        std::array<int16_t, 960> out{};
        core.render(out.data(),480,{false,true});
        CoreSnapshot s{};
        expect(core.pop_snapshot(s) && s.rendered==480,
               "explicit concealment preserves sample continuity");
        core.render(out.data(),480,{false,true});
        expect(core.pop_snapshot(s) && s.concealed_outputs>0,
               "concealed output uses actual tagged interpolation taps");
    }
    {   // Two-block concealment under pressure: first concealed block fits,
        // second is rejected. That partial concealment MUST be retired before
        // any later real PCM can share the ring with it.
        RenderCore core;
        for (int i=0;i<15;++i) enqueue(core,i*240,12);
        enqueue(core,15*240,0,true);
        const auto zero=block(0), real=block(500);
        expect(core.publish(zero.data(),240,tag(16*240,1,7,1,11,true))==
               Admission::Overflow, "second concealment partial admission overflows");
        expect(core.publish(real.data(),240,tag(17*240))==
               Admission::RecoverPending, "no real audio joins partial concealment");
        std::array<int16_t,192> out{};
        core.render(out.data(),96,{false,false});
        CoreSnapshot rec{};
        expect(core.pop_snapshot(rec) && rec.discarded==3840,
               "callback discards partial concealment and all retired old PCM");
        expect(core.publish(real.data(),240,tag(17*240))==
               Admission::Accepted, "next authenticated forward anchor starts fresh playback");
    }
    {   // Duplicate, overlapping or changed identities cannot join FIFO.
        RenderCore core;
        prime(core,0);
        auto a=block(400);
        expect(core.publish(a.data(),240,tag(7*240))==Admission::Stale,
               "reject duplicate/overlap");
        expect(core.publish(a.data(),240,tag(8*240,2))==
               Admission::Discontinuity,"epoch changed => recovery");
        auto pending=core.requested_recoveries();
        expect(core.publish(a.data(),240,tag(8*240,2)) ==
               Admission::RecoverPending,"pending invalidation blocks producer");
        std::array<int16_t,96> out{};
        core.render(out.data(),48,{false,false});
        expect(core.applied_recoveries()==pending,"callback applies generation");
        expect(core.publish(a.data(),240,tag(8*240,2))==
               Admission::Accepted,"explicit new epoch anchor");
        expect(core.publish(a.data(),240,tag(9*240,2,9))==
               Admission::Discontinuity,"stream change needs recovery");
    }
    {   // Full ring is never overwritten. Invalidation can't be lost even
        // when repeatedly requested; only callback reclaims the old tail.
        RenderCore core;
        for (int i=0;i<16;++i) enqueue(core,i*240,123);
        const auto a=block(124);
        expect(core.publish(a.data(),240,tag(3840))==Admission::Overflow,
               "hard 80ms capacity");
        const uint64_t first_ticket=core.requested_recoveries();
        for (int i=0;i<500;++i) core.invalidate();
        expect(core.requested_recoveries()==first_ticket+500,
               "critical recovery requests coalesce without overwrite");
        std::array<int16_t,192> out{};
        core.render(out.data(),96,{false,false});
        expect(core.read()==core.write() &&
               core.applied_recoveries()==core.requested_recoveries(),
               "callback alone releases source storage after overflow");
        CoreSnapshot snap{};
        expect(core.pop_snapshot(snap) &&
               snap.discarded==3840 && snap.invalidations>=1,
               "overflow discard accounted");
        expect(core.publish(a.data(),240,tag(3000))==Admission::Stale,
               "delayed old packet rejected");
        expect(core.publish(a.data(),240,tag(4080))==Admission::Accepted,
               "new anchored source after overflow");
    }
    {   // A real underrun initiates recovery without producer touching the
        // consumer read index; stale packets cannot fill the fresh epoch.
        RenderCore core;
        prime(core);
        std::array<int16_t,1024*2> out{};
        core.render(out.data(),1024,{false,false});
        core.render(out.data(),1024,{false,false});
        expect(core.requested_recoveries()>core.applied_recoveries(),
               "underflow raises lossless callback invalidation");
        core.render(out.data(),96,{false,false});
        auto sample=block(12);
        expect(core.publish(sample.data(),240,tag(48100))==Admission::Stale,
               "late packet after underrun rejected");
        // Callback-induced recovery has new playout identity; re-anchor.
        expect(core.publish(sample.data(),240,tag(50000))==Admission::Accepted,
               "fresh forward source anchor after callback-owned generation reset");
    }
    {   // Pause at the exact old check/store race. A pending recovery must
        // not complete while a producer owns an unpublished reservation.
        // Invoke the callback owner deterministically from the test seam;
        // production contains neither the seam nor any blocking test code.
        for (int mode=0; mode<2; ++mode) {
            RenderCore core;
            prime(core,0);
            struct PausedPublication {
                RenderCore* core;
                bool invoked = false;
                uint64_t applied_during = UINT64_MAX;
                uint64_t requested_during = 0;
                bool deferred_output_silent = false;
                static void run(void* context, RenderProbe point) noexcept {
                    auto& self = *static_cast<PausedPublication*>(context);
                    if (point != RenderProbe::BeforePublicationCommit || self.invoked)
                        return;
                    self.invoked = true;
                    std::array<int16_t,1024*2> output{};
                    self.core->render(output.data(),1024,{false,false});
                    self.core->render(output.data(),1024,{false,false});
                    // This callback used to apply recovery before the
                    // producer resumed and published its old-generation PCM.
                    self.core->render(output.data(),96,{false,false});
                    self.applied_during = self.core->applied_recoveries();
                    self.requested_during = self.core->requested_recoveries();
                    self.deferred_output_silent = std::all_of(
                        output.begin(), output.begin()+96*2,
                        [](int16_t value) { return value == 0; });
                }
            } pause{&core};
            core.test_set_probe(&PausedPublication::run,&pause);
            const auto retired = block(2999);
            expect(core.publish_guarded(retired.data(),240,tag(1920),0)==
                   Admission::Accepted,"in-flight reservation resolves before reset");
            core.test_set_probe(nullptr,nullptr);
            expect(pause.invoked && pause.requested_during>0 &&
                   pause.applied_during==0 && pause.deferred_output_silent,
                   "callback never waits or acknowledges an active reservation");
            std::array<int16_t,480> output{};
            core.render(output.data(),96,{false,false});
            expect(core.applied_recoveries()==core.requested_recoveries() &&
                   core.read()==core.write(),
                   "completed recovery discards even the late old reservation");
            CoreSnapshot record{}, latest{};
            while(core.pop_snapshot(record)) latest=record;
            expect(latest.discarded==240,"late reservation discard is accounted");
            const auto fresh = block(600);
            const uint64_t authorization = core.applied_recoveries();
            expect(core.publish_guarded(fresh.data(),240,tag(2160),0)==
                   Admission::RecoverPending,"retired authorization cannot reanchor PCM");
            for (uint32_t i=0;i<6;++i)
                expect(core.publish_guarded(fresh.data(),240,tag(2160+i*240),
                           authorization)==Admission::Accepted,
                       "authorized new generation primes independently");
            core.render(output.data(),240,{false,mode!=0});
            expect(std::all_of(output.begin(),output.end(),
                       [](int16_t value) { return value == 600; }),
                   "no retired sample escapes in linear or identity mode");
            expect(core.pop_snapshot(record) && record.playout==2 &&
                   record.last_source>=2160,
                   "only the active playout generation contributes output");
        }
    }
    {   // An active-generation guard is mandatory even for identity output,
        // which has no interpolation pair whose boundary could catch this.
        for (int mode=0;mode<2;++mode) {
            RenderCore core;
            prime(core,0);
            core.test_set_tag(0,tag(0)); // playout 0, active generation is 1
            std::array<int16_t,96> output{};
            core.render(output.data(),48,{false,mode!=0});
            CoreSnapshot record{};
            expect(core.pop_snapshot(record) && record.rendered==0 &&
                   record.silence==48 &&
                   core.requested_recoveries()>core.applied_recoveries(),
                   "retired first tap never contributes in either output mode");
        }
    }
    {   // The producer may not observe recovery complete before the discarded
        // full-ring tail is released. An attempt during that handoff is
        // pending, never a spurious second overflow/recovery request.
        RenderCore core;
        for (int i=0;i<16;++i) enqueue(core,i*240,12);
        core.invalidate();
        const uint64_t requested = core.requested_recoveries();
        struct PausedTailRelease {
            RenderCore* core;
            bool invoked = false;
            uint64_t applied_before_tail = UINT64_MAX;
            uint64_t tail_before_release = UINT64_MAX;
            Admission admission = Admission::Invalid;
            static void run(void* context, RenderProbe point) noexcept {
                auto& self = *static_cast<PausedTailRelease*>(context);
                if (point != RenderProbe::BeforeRecoveryTailRelease) return;
                self.invoked = true;
                self.applied_before_tail = self.core->applied_recoveries();
                self.tail_before_release = self.core->read();
                const auto fresh = block(700);
                self.admission = self.core->publish(fresh.data(),240,tag(3840));
            }
        } pause{&core};
        core.test_set_probe(&PausedTailRelease::run,&pause);
        std::array<int16_t,192> output{};
        core.render(output.data(),96,{false,false});
        core.test_set_probe(nullptr,nullptr);
        expect(pause.invoked && pause.applied_before_tail==0 &&
               pause.tail_before_release==0 &&
               pause.admission==Admission::RecoverPending,
               "acknowledgement follows discarded-tail release");
        expect(core.applied_recoveries()==requested &&
               core.requested_recoveries()==requested && core.overflow()==0 &&
               core.read()==core.write(),
               "handoff creates neither false overflow nor redundant recovery");
        const auto fresh=block(700);
        expect(core.publish_guarded(fresh.data(),240,tag(3840),0)==
               Admission::RecoverPending,"old recovery token is rejected");
        expect(core.publish_guarded(fresh.data(),240,tag(3840),requested+1)==
               Admission::RecoverPending,"unapplied future token is rejected");
        expect(core.publish_guarded(fresh.data(),240,tag(3840),requested)==
               Admission::Accepted,"exact applied token authorizes fresh publication");
    }
    {   // Concurrent producer and callback, with a third off-callback
        // telemetry reader. Mailbox slots use SPSC release/acquire ownership, not
        // seqlocks over non-atomic C++ payload.
        RenderCore core;
        std::atomic<bool> done=false;
        std::atomic<uint64_t> success=0, samples=0;
        std::thread producer([&] {
            for (uint64_t i=0;i<4000;++i) {
                auto pcm=block(11);
                const auto result=core.publish(pcm.data(),240,tag(i*240));
                if(result==Admission::Accepted) ++success;
                else if(result==Admission::RecoverPending) { }
                else if(result==Admission::Overflow) { }
                if(i%7==0) std::this_thread::yield();
            }
            done.store(true,std::memory_order_release);
        });
        std::thread render([&] {
            std::array<int16_t,960*2> out{};
            int count=0, drained=0;
            while(!done.load(std::memory_order_acquire)||drained++<50) {
                const uint32_t sizes[]={48,96,192,240,480,960};
                core.render(out.data(),sizes[count%6],{false,false});
                ++count;
                if(count%3==0) std::this_thread::yield();
            }
        });
        std::thread observer([&] {
            CoreSnapshot record{};
            uint64_t last=0;
            while(!done.load(std::memory_order_acquire)) {
                if(core.pop_snapshot(record)) {
                    expect(record.callback_seq>last,"coherent monotonic callback snapshot");
                    expect(record.released<=record.published_tail,
                           "release never exceeds committed publication");
                    expect(record.earliest_needed<=record.published_tail,
                           "consumer tail stays bounded by publication");
                    last=record.callback_seq;
                    ++samples;
                } else std::this_thread::yield();
            }
        });
        producer.join();render.join();observer.join();
        expect(core.read()<=core.write(),"threaded SPSC ownership");
        expect(success.load()>0,"concurrent producer publishes valid frames");
        expect(samples.load()+core.dropped_snapshots()>0,"nonblocking callback telemetry");
    }
    std::puts("PASS MP-001 actual AAudio render core: boundaries, interpolation support, overlap, context, loss, recovery, full ring, concurrent SPSC, coherent snapshots");
}
