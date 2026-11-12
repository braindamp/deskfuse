#pragma once
// Diagnostic ABI 1 is separate from, and does not change, the mapped PCM ABI.
struct DeskFusePcmTraceEvent {
    unsigned long long sequence, serial, generation;
    unsigned int kind, streams, denials, flags, pin;
    GUID mode;
    deskfuse::PcmGeometry geometry;
    unsigned int alignment;
};
struct DeskFusePcmBindDiagnostic {
    unsigned int size, abi, stage, status;
    GUID source;
    unsigned long long generation, owner, consumer, selected;
    unsigned int flags, streams, firstDenial, denials, count, next;
    deskfuse::PcmGeometry geometry;
    GUID mode;
    unsigned int pin, alignment;
    DeskFusePcmTraceEvent events[8];
};
static_assert(sizeof(DeskFusePcmTraceEvent)==88,"trace event ABI");
static_assert(sizeof(DeskFusePcmBindDiagnostic)==840,"bind diagnostic ABI");
inline void DeskFuseTraceAppend(DeskFusePcmBindDiagnostic& d,DeskFusePcmTraceEvent e) {
    e.sequence=d.count?d.events[(d.next+7)%8].sequence+1:1;
    d.events[d.next]=e;d.next=(d.next+1)%8;if(d.count<8)++d.count;
}
inline bool DeskFuseTraceDeny(DeskFusePcmBindDiagnostic& d,unsigned int reason) {
    const auto before=d.denials;if(!d.firstDenial)d.firstDenial=reason;d.denials|=reason;return before!=d.denials;
}
