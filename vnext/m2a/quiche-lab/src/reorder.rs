//! Fixed-capacity, connection-owner-only staging for a real source hole.
//! Contiguous PCM never waits. The timer is armed only for an actual hole,
//! and can consume at most 10 ms of existing native playout headroom.
use crate::Header;
use std::time::{Duration, Instant};

pub const CAPACITY: usize = 8;
const HOLD_LIMIT: Duration = Duration::from_millis(10);
pub const MEDIA_BYTES: usize = 1000;
pub const TOTAL_FUTURE_FRAMES:u64 = 3840; // existing 80ms budget, not per queue

#[derive(Clone, Copy)]
pub struct Media {
    pub header: Header,
    pub bytes: [u8; MEDIA_BYTES],
}
#[derive(Default, Clone, Copy)]
pub struct Headroom {
    pub primed: bool,
    pub queued_frames: u64,
    pub callback_frames: u32,
}
impl Headroom {
    fn wait(self) -> Duration {
        if !self.primed { return HOLD_LIMIT; }
        // Keep two maximum callbacks as scheduling/support margin. This is
        // a conservative admission estimate, never acoustic timing truth.
        let reserve = u64::from(self.callback_frames.max(240)) * 2;
        Duration::from_nanos(self.queued_frames.saturating_sub(reserve)
            .saturating_mul(1_000_000_000) / 48_000).min(HOLD_LIMIT)
    }
}
pub struct Reorder {
    slots: [Option<Media>; CAPACITY],
    hole: Option<(u64, Instant)>,
    pub full_drops: u64,
    pub duplicates: u64,
    pub budget_drops: u64,
}
impl Default for Reorder {
    fn default() -> Self {
        Self { slots: [None; CAPACITY], hole: None, full_drops: 0, duplicates: 0, budget_drops:0 }
    }
}
impl Reorder {
    pub fn clear(&mut self) { self.slots.fill(None); self.hole = None; }
    pub fn staged_frames(&self)->u64 {self.slots.iter().flatten().count() as u64 * 240}
    /// Span includes any missing source frames which would need concealment,
    /// not merely the bytes already received. Both stores share one budget.
    pub fn fits_budget(&self,header:Header,expected:Option<u64>,native_queued:u64)->bool {
        let first=self.slots.iter().flatten().map(|m|m.header.first_source_frame)
            .chain(std::iter::once(header.first_source_frame)).min().unwrap();
        let end=self.slots.iter().flatten().map(|m|m.header.first_source_frame.saturating_add(240))
            .chain(std::iter::once(header.first_source_frame.saturating_add(240))).max().unwrap();
        let floor=expected.map_or(first,|floor|floor.min(first));
        native_queued.saturating_add(end.saturating_sub(floor))<=TOTAL_FUTURE_FRAMES
    }
    pub fn push(&mut self, header: Header, bytes: &[u8]) -> bool {
        if bytes.len() != MEDIA_BYTES { return false; }
        if self.slots.iter().flatten().any(|m|
            m.header.first_source_frame == header.first_source_frame ||
            m.header.packet_sequence == header.packet_sequence) {
            self.duplicates = self.duplicates.saturating_add(1);
            return false;
        }
        let Some(slot) = self.slots.iter_mut().find(|s| s.is_none()) else {
            self.full_drops = self.full_drops.saturating_add(1);
            return false;
        };
        let mut media = Media { header, bytes: [0; MEDIA_BYTES] };
        media.bytes.copy_from_slice(bytes);
        *slot = Some(media);
        true
    }
    pub fn deadline(&self) -> Option<Instant> { self.hole.map(|(_, at)| at) }
    /// During a kernel receive batch, `commit_hole` is false: even zero
    /// headroom must allow A,C,B already in that batch to become A,B,C.
    /// The caller rechecks authoritative headroom after the batch and wakes
    /// on this one-shot deadline; owner descheduling never extends it.
    pub fn pop(&mut self, expected: Option<u64>, now: Instant,
               headroom: Option<Headroom>, commit_hole: bool) -> Option<Media> {
        // Frontiers are irreversible; a staged packet retired meanwhile
        // cannot delay or overwrite a fresh interval.
        if let Some(floor) = expected {
            for slot in &mut self.slots {
                if slot.as_ref().is_some_and(|m| m.header.first_source_frame < floor) {
                    *slot = None;
                }
            }
        }
        let index = self.slots.iter().enumerate().filter_map(|(i,m)|
            m.as_ref().map(|m| (i,m.header.first_source_frame)))
            .min_by_key(|(_,frame)| *frame);
        let Some((index,_)) = index else { self.hole=None; return None; };
        let frame = self.slots[index].as_ref().unwrap().header.first_source_frame;
        let Some(expected) = expected else {
            self.hole = None;
            return self.slots[index].take();
        };
        if frame == expected {
            self.hole = None;
            return self.slots[index].take();
        }
        let proposed = now + headroom.map_or(Duration::ZERO, Headroom::wait);
        let due = match self.hole {
            Some((floor, due)) if floor == expected => due.min(proposed),
            _ => proposed,
        };
        self.hole = Some((expected, due));
        if !commit_hole || now < due { return None; }
        self.hole = None;
        self.slots[index].take()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn h(sequence:u32) -> Header {
        Header { version:1,kind:1,flags:0,header_bytes:40,frame_count:240,
            media_epoch:1,source_stream_id:1,format_config_id:1,
            packet_sequence:sequence,first_source_frame:48_000+u64::from(sequence)*240,
            payload_bytes:960,clock_anchor_id:17 }
    }
    fn push(q:&mut Reorder,n:u32) { assert!(q.push(h(n), &[0;MEDIA_BYTES])); }
    fn room() -> Option<Headroom> {
        Some(Headroom {primed:true,queued_frames:1920,callback_frames:240})
    }
    #[test]
    fn same_batch_reordering_does_not_conceal_even_without_headroom() {
        let now=Instant::now(); let mut q=Reorder::default();
        push(&mut q,0); assert_eq!(q.pop(None,now,None,false).unwrap().header.packet_sequence,0);
        push(&mut q,2); assert!(q.pop(Some(48240),now,None,false).is_none());
        push(&mut q,1);
        assert_eq!(q.pop(Some(48240),now,None,false).unwrap().header.packet_sequence,1);
        assert_eq!(q.pop(Some(48480),now,None,false).unwrap().header.packet_sequence,2);
    }
    #[test]
    fn late_packet_within_existing_headroom_recovers_without_moving_deadline() {
        let now=Instant::now(); let mut q=Reorder::default(); push(&mut q,2);
        assert!(q.pop(Some(48240),now,room(),true).is_none());
        assert_eq!(q.deadline(),Some(now+HOLD_LIMIT));
        push(&mut q,1);
        assert_eq!(q.pop(Some(48240),now+Duration::from_millis(9),room(),true).unwrap().header.packet_sequence,1);
        assert_eq!(q.pop(Some(48480),now+Duration::from_millis(9),room(),true).unwrap().header.packet_sequence,2);
    }
    #[test]
    fn true_hole_commits_at_deadline_and_stalls_do_not_extend_it() {
        let now=Instant::now(); let mut q=Reorder::default(); push(&mut q,2);
        assert!(q.pop(Some(48240),now,room(),true).is_none());
        assert!(q.pop(Some(48240),now+Duration::from_millis(9),room(),true).is_none());
        assert_eq!(q.deadline(),Some(now+HOLD_LIMIT));
        assert_eq!(q.pop(Some(48240),now+Duration::from_millis(100),room(),true).unwrap().header.packet_sequence,2);
    }
    #[test]
    fn headroom_loss_shortens_wait_and_recovery_clears_owned_pcm() {
        let now=Instant::now(); let mut q=Reorder::default(); push(&mut q,2);
        assert!(q.pop(Some(48240),now,room(),true).is_none());
        assert_eq!(q.pop(Some(48240),now+Duration::from_millis(1),None,true).unwrap().header.packet_sequence,2);
        for n in 0..CAPACITY as u32 { push(&mut q,n); }
        assert!(!q.push(h(0), &[0;MEDIA_BYTES])); assert_eq!(q.duplicates,1);
        assert!(!q.push(h(9), &[0;MEDIA_BYTES])); assert_eq!(q.full_drops,1);
        q.clear(); assert!(q.pop(None,now,None,true).is_none()); assert!(q.deadline().is_none());
    }
    #[test]
    fn staged_pcm_and_hole_span_share_native_eighty_ms_budget() {
        let mut q=Reorder::default(); push(&mut q,2);
        assert_eq!(q.staged_frames(),240);
        assert!(q.fits_budget(h(3),Some(48240),3120)); // 3blocks including hole
        assert!(!q.fits_budget(h(3),Some(48240),3121));
        assert!(!q.fits_budget(h(20),Some(48240),0)); // huge gap is not free
    }
}
