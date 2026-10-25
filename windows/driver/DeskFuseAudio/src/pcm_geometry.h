#pragma once
namespace deskfuse {
struct PcmGeometry {
    unsigned int state, packetFrames, notifications, sampleRate, channels, bits;
};
inline bool valid_notifications(unsigned int n) { return n==1 || n==2; }
inline PcmGeometry pcm_geometry(unsigned int n,unsigned int packet,unsigned int rate,unsigned int channels,unsigned int bits) {
    const bool known=packet!=0; // allocated non-notification buffers are known unsupported
    const bool qualified=known && n==2 && packet<=240 && rate==48000 && channels==2 && bits==16;
    return {known?(qualified?1u:2u):0u,packet,n,rate,channels,bits};
}
inline bool same_geometry(const PcmGeometry& a,const PcmGeometry& b) {
    return a.state==b.state && a.packetFrames==b.packetFrames && a.notifications==b.notifications &&
        a.sampleRate==b.sampleRate && a.channels==b.channels && a.bits==b.bits;
}
}
