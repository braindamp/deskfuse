#pragma once
#if !defined(_KERNEL_MODE)
#include <stdint.h>
#endif
namespace deskfuse {
// Caller holds the source publication lock. Reserved slots consume BOTH
// budgets before copying; acknowledgements cannot release an in-flight slot.
struct PcmCredits {
    struct Slot {unsigned frames;unsigned long long ticket;bool ready;};
    Slot slots[8];
    unsigned long long nextTicket,acknowledged;
    unsigned outstandingFrames;
    bool can_reserve(unsigned frames) const {
        return frames && frames<=960 && nextTicket!=~0ull && nextTicket-acknowledged<8 &&
            outstandingFrames<=960 && frames<=960-outstandingFrames;
    }
    unsigned long long reserve(unsigned frames) {
        if (!can_reserve(frames)) return 0;
        const auto ticket=++nextTicket;slots[(ticket-1)%8]={frames,ticket,false};outstandingFrames+=frames;return ticket;
    }
    bool publish(unsigned long long ticket) {
        if (ticket<=acknowledged || ticket>nextTicket) return false;
        auto& slot=slots[(ticket-1)%8];
        if (slot.ticket!=ticket || slot.ready) return false;
        slot.ready=true;return true;
    }
    bool ack(unsigned long long ticket) {
        if (ticket<acknowledged || ticket>nextTicket) return false;
        for (auto t=acknowledged;t<ticket;) {
            ++t;
            const auto& slot=slots[(t-1)%8];if (!slot.ready || slot.ticket!=t) return false;
        }
        while (acknowledged<ticket) {
            auto& slot=slots[acknowledged%8];outstandingFrames-=slot.frames;slot={};++acknowledged;
        }
        return true;
    }
    bool may_arm(unsigned long long observed,unsigned long long current,bool waiting) const {
        return observed==current && (waiting || acknowledged==nextTicket);
    }
};
}
