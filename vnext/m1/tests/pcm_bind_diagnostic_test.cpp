#include <Windows.h>
#include <cassert>
#include "../../../windows/driver/DeskFuseAudio/src/pcm_geometry.h"
#include "../../../windows/driver/DeskFuseAudio/src/pcm_bind_diagnostic.h"
int main() {
    DeskFusePcmBindDiagnostic live={};
    for(unsigned i=1;i<=20;++i) {DeskFusePcmTraceEvent e={};e.serial=i;DeskFuseTraceAppend(live,e);}
    assert(live.count==8);
    for(unsigned i=0;i<8;++i) {const auto& e=live.events[(live.next+i)%8];assert(e.sequence==13+i && e.serial==13+i);}
    assert(DeskFuseTraceDeny(live,1));assert(!DeskFuseTraceDeny(live,1));
    assert(DeskFuseTraceDeny(live,64));assert(live.firstDenial==1 && live.denials==65);
    const auto saved=live;live={};
    assert(saved.count==8 && saved.denials==65 && saved.events[(saved.next+7)%8].sequence==20);
    return 0;
}
