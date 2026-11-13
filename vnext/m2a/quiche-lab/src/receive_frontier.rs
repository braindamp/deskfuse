//! Active authenticated PCM rejection frontier for one established QUIC
//! connection. Never rolled back by Android playout generation recovery.
//! A forward DATAGRAM is an explicit source anchor only in the *same*
//! authenticated epoch/stream/format context; changed context needs a new
//! negotiated configuration, and the lab fails closed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Context {pub epoch:u32,pub stream:u32,pub format:u32}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Reject {Late,Overlapping,RetiredContext,Overflow}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Accepted {
    pub missing_sequences:u64,
    pub missing_frames:u64,
    pub expected_source:u64,
    pub newly_anchored:bool,
}
/// An uninterrupted authenticated sequence with a source-frame hole is
/// NOT QUIC packet loss: the sender/capture pipeline omitted PCM before
/// assigning media sequences. Isolated holes remain observable; a sustained
/// 5-ms-on / 5-ms-missing pattern must stop adaptive playback rather than
/// generate a full-scale burst of alternating concealment and real audio.
#[derive(Default)]
pub struct ContinuityWatch {
    repeated_gaps:u32,
}
impl ContinuityWatch {
    pub const LIMIT:u32=8;
    /// Called only for accepted packets. `previous_sequence` is read from
    /// the authenticated receive frontier immediately BEFORE ingest.
    pub fn observe(&mut self,previous_sequence:Option<u32>,sequence:u32,
                   missing_frames:u64)->bool {
        let contiguous=previous_sequence.is_some_and(|last|
            last.checked_add(1)==Some(sequence));
        if contiguous && missing_frames>0 {
            self.repeated_gaps=self.repeated_gaps.saturating_add(1);
        } else {
            self.repeated_gaps=0;
        }
        self.repeated_gaps>=Self::LIMIT
    }
    pub fn repeated_gaps(&self)->u32 {self.repeated_gaps}
}

/// A single-owner checked candidate. Preparation alone retires no source audio.
#[must_use]
pub struct Prepared {
    pub accepted: Accepted,
    context: Context,
    sequence: u32,
    end_frame: u64,
    prior_context: Option<Context>,
    prior_sequence: Option<u32>,
    prior_floor: u64,
}
#[derive(Default)]
pub struct ReceiveFrontier {
    context:Option<Context>,
    last_sequence:Option<u32>,
    next_frame:u64,
}
impl ReceiveFrontier {
    /// Pure checked admission; does not advance the rejection frontier.
    pub fn prepare(&self, context:Context, sequence:u32, first_frame:u64,
                   frames:u64)->Result<Prepared,Reject>{
        let Some(end_frame)=first_frame.checked_add(frames) else {return Err(Reject::Overflow)};
        if frames==0 {return Err(Reject::Overflow)}
        if self.context.is_some_and(|previous|previous!=context) {
            return Err(Reject::RetiredContext);
        }
        let previous_seq=self.last_sequence;
        if let Some(last)=previous_seq {
            if sequence<=last {return Err(Reject::Late)}
            if first_frame<self.next_frame {return Err(Reject::Overlapping)}
        }
        let expected=if previous_seq.is_some(){self.next_frame}else{first_frame};
        Ok(Prepared {
            accepted:Accepted {
                missing_sequences: previous_seq.map_or(0,|v|u64::from(sequence-v-1)),
                missing_frames: first_frame-expected,
                expected_source:expected,
                newly_anchored:previous_seq.is_none(),
            },
            context,sequence,end_frame,
            prior_context:self.context,prior_sequence:previous_seq,prior_floor:self.next_frame,
        })
    }
    /// Native Accepted is the publication commit point, not proof of audible
    /// presentation. No other owner can mutate this Rust cursor in between.
    pub fn commit_published(&mut self, prepared:Prepared) {
        self.finish(prepared);
    }
    /// Explicitly retire an abandoned interval without claiming publication.
    pub fn retire(&mut self, prepared:Prepared) {
        self.finish(prepared);
    }
    fn finish(&mut self, prepared:Prepared) {
        debug_assert_eq!(self.context,prepared.prior_context);
        debug_assert_eq!(self.last_sequence,prepared.prior_sequence);
        debug_assert_eq!(self.next_frame,prepared.prior_floor);
        self.context=Some(prepared.context);
        self.last_sequence=Some(prepared.sequence);
        self.next_frame=prepared.end_frame;
    }
    /// Verify-only callers without a native renderer still retire validated
    /// received source intervals, without claiming publication.
    pub fn ingest(&mut self, context:Context, sequence:u32, first_frame:u64,
                  frames:u64)->Result<Accepted,Reject>{
        let prepared=self.prepare(context,sequence,first_frame,frames)?;
        let accepted=prepared.accepted;
        self.retire(prepared);
        Ok(accepted)
    }
    /// Called only after a matching, authenticated Windows fresh-capture
    /// nonce response. Advance the irrecoverable floor to that NEW source
    /// observation. Never decrease it, even across AAudio re-prime.
    pub fn accept_fresh_anchor(&mut self, frame:u64)->Result<(),Reject>{
        if frame<self.next_frame{return Err(Reject::Overlapping);}
        if self.context.is_none(){return Err(Reject::RetiredContext);}
        self.next_frame=frame;
        Ok(())
    }
    pub fn floor(&self)->u64{self.next_frame}
    pub fn last_sequence(&self)->Option<u32>{self.last_sequence}
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn checked_candidate_requires_explicit_publication_or_retirement() {
        let ctx=Context{epoch:1,stream:1,format:1};
        let mut frontier=ReceiveFrontier::default();
        let first=frontier.prepare(ctx,3,48_000,240).unwrap();
        assert_eq!(frontier.floor(),0);
        assert_eq!(frontier.last_sequence(),None);
        frontier.commit_published(first);
        assert_eq!(frontier.floor(),48_240);
        let abandoned=frontier.prepare(ctx,4,48_240,240).unwrap();
        assert_eq!(frontier.floor(),48_240);
        frontier.retire(abandoned);
        assert_eq!(frontier.floor(),48_480);
        assert_eq!(frontier.prepare(ctx,4,48_240,240).err(),Some(Reject::Late));
    }
    #[test]
    fn sustained_source_holes_with_contiguous_packet_sequences_fail_closed(){
        let context=Context{epoch:1,stream:1,format:1};
        let mut f=ReceiveFrontier::default();
        let mut watch=ContinuityWatch::default();
        let first=f.ingest(context,0,48000,240).unwrap();
        assert!(!watch.observe(None,0,first.missing_frames));
        for seq in 1..ContinuityWatch::LIMIT {
            let last=f.last_sequence();
            let accepted=f.ingest(context,seq,48000+u64::from(seq)*480,240).unwrap();
            assert_eq!(accepted.missing_frames,240);
            assert_eq!(accepted.missing_sequences,0);
            assert!(!watch.observe(last,seq,accepted.missing_frames));
        }
        assert_eq!(watch.repeated_gaps(),ContinuityWatch::LIMIT-1);
        let last=f.last_sequence();
        let seq=ContinuityWatch::LIMIT;
        let next=f.ingest(context,seq,48000+u64::from(seq)*480,240).unwrap();
        assert!(watch.observe(last,seq,next.missing_frames));
        // No successive-hole state survives a genuine continuous 240-frame
        // source interval or a packet-sequence discontinuity.
        let last=f.last_sequence();
        let good=f.ingest(context,seq+1,48000+u64::from(seq)*480+240,240).unwrap();
        assert!(!watch.observe(last,seq+1,good.missing_frames));
        assert_eq!(watch.repeated_gaps(),0);
        let last=f.last_sequence();
        let gap=f.ingest(context,seq+4,60000,240).unwrap();
        assert!(gap.missing_frames>0 && gap.missing_sequences>0);
        assert!(!watch.observe(last,seq+4,gap.missing_frames));
    }

    #[test]
    fn delayed_and_repeated_packets_can_never_revive_retired_ranges(){
        let a=Context{epoch:1,stream:7,format:3};
        let b=Context{epoch:2,..a};
        let mut f=ReceiveFrontier::default();
        assert!(f.ingest(a,10,48000,240).unwrap().newly_anchored);
        assert_eq!(f.ingest(a,11,48240,240).unwrap().missing_frames,0);
        assert_eq!(f.ingest(a,11,48240,240),Err(Reject::Late));
        assert_eq!(f.ingest(a,12,48240,240),Err(Reject::Overlapping));
        let gap=f.ingest(a,15,49200,240).unwrap();
        assert_eq!((gap.missing_sequences,gap.missing_frames),(3,720));
        // Caller can re-prime/retire its output buffer without lowering floor.
        assert_eq!(f.ingest(a,13,48480,240),Err(Reject::Late));
        assert_eq!(f.ingest(a,16,48500,240),Err(Reject::Overlapping));
        assert_eq!(f.ingest(b,16,49440,240),Err(Reject::RetiredContext));
        assert_eq!(f.floor(),49440);
        assert_eq!(f.ingest(a,16,49440,240).unwrap().missing_frames,0);
        assert_eq!(f.last_sequence(),Some(16));
        assert_eq!(f.accept_fresh_anchor(60000),Ok(()));
        assert_eq!(f.floor(),60000);
        assert_eq!(f.accept_fresh_anchor(59999),Err(Reject::Overlapping));
        assert_eq!(f.ingest(a,17,60000,240).unwrap().missing_frames,0);
        assert_eq!(f.ingest(a,18,59000,240),Err(Reject::Overlapping));
    }
}
