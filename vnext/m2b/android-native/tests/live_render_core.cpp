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
    {   // Concurrent producer and callback, with a third off-callback
        // telemetry reader. All mailbox slots use CAS ownership, not
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
            int count=0;
            while(!done.load(std::memory_order_acquire)||count++<50) {
                const uint32_t sizes[]={48,96,192,240,480,960};
                core.render(out.data(),sizes[count%6],{false,false});
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
