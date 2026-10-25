#include "../../../windows/driver/DeskFuseAudio/src/pcm_credits.h"
#include <cstdlib>
#include <cstdio>
static void require(bool value){if(!value)std::abort();}
int main(){
    deskfuse::PcmCredits ring{};
    const auto reserved=ring.reserve(960);require(reserved==1 && !ring.can_reserve(1));
    require(!ring.ack(reserved)); // in-flight copy owns all 960 credits
    require(ring.outstandingFrames==960 && ring.acknowledged==0);
    require(!ring.may_arm(4,5,false)); // control changed after consumer snapshot
    require(!ring.may_arm(5,5,false)); // reservation not yet published
    require(ring.publish(reserved) && ring.ack(reserved));
    require(ring.outstandingFrames==0 && ring.may_arm(5,5,false));
    for(unsigned round=0;round<1000;++round){
        unsigned long long last=0;
        for(unsigned i=0;i<8;++i){last=ring.reserve(1);require(last!=0);}
        require(!ring.can_reserve(1)); // descriptor count independently bounded
        require(!ring.ack(last));
        for(auto t=ring.acknowledged+2;t<=last;++t)require(ring.publish(t));
        require(!ring.ack(last)); // later publications cannot release the first in-flight copy
        require(ring.publish(ring.acknowledged+1));require(ring.ack(last));
        require(!ring.ack(last-1) && !ring.ack(last+1)); // replay / forged credit
        require(ring.outstandingFrames==0);
    }
    require(ring.reserve(0)==0 && ring.reserve(961)==0);
    require(ring.may_arm(9,9,true)); // pre-RUN data waits for a new control transition
    require(!ring.may_arm(9,10,true));
    std::puts("actual credit core: in-flight ownership, bounds, replay, wrap, atomic rearm passed");
}
