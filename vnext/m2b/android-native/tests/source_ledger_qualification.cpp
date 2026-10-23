#include "source_ledger.h"
#include <cstdio>
#include <stdexcept>
using namespace df_audio;
void check(bool ok, const char* msg) {
    if (!ok) throw std::runtime_error(msg);
}
int main() {
    SourceLedger ledger;
    check(ledger.publish(0, 0, 48000, 240, 1, 1, FrameKind::Captured),
          "publish real PCM");
    check(!ledger.publish(240, 0, 48000, 240, 1, 1, FrameKind::Captured),
          "reject overlapping source identity");
    check(ledger.commit(0, 0x80000000), "commit fractional source phase");
    check(!ledger.commit(0, 0x40000000),
          "reject retroactive fractional phase rewind");
    check(ledger.commit(1, 0), "commit next physical source frame");
    check(ledger.release(0, 2, 240), "release exactly consumed source frames");
    check(ledger.publish(240, 2, 48480, 240, 1, 1, FrameKind::Concealed),
          "preserve missing physical frame identity as concealed PCM");
    check(ledger.counters.source_gap_frames == 240,
          "account explicitly missing source frames before concealment");
    check(ledger.commit(240, 0), "commit concealed source");
    check(ledger.counters.committed_concealment_frames == 1,
          "concealed output distinguishable from captured PCM");
    check(ledger.release(2, 480, 480, true),
          "rebuffer flush discards previously published unplayed frames");
    check(ledger.counters.discarded_on_rebuffer_frames == 478,
          "count discarded source intervals on rebuffer");
    check(ledger.publish(480, 480, 100, 240, 2, 2, FrameKind::Captured),
          "new epoch/generation may start independent source identity");
    check(ledger.commit(480, 0), "commit new generation");
    check(ledger.counters.last_epoch == 2 &&
          ledger.counters.last_generation == 2 &&
          ledger.counters.last_committed_source_frame == 100,
          "last committed output identifies exact new generation and phase");
    ledger.output_silence(48);
    check(ledger.counters.output_silence_frames == 48,
          "silence committed separately from source-frame publication");
    check(!ledger.publish(kRingFrames, 0, 0, 240, 3, 3, FrameKind::Captured),
          "bounded ring overflow must reject, not overwrite");
    std::puts("PASS MP001 identity: publish/commit/release, Q32 phase, gaps, concealment, rebuffer, epoch/generation");
}
