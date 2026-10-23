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
#[derive(Default)]
pub struct ReceiveFrontier {
    context:Option<Context>,
    last_sequence:Option<u32>,
    next_frame:u64,
}
impl ReceiveFrontier {
    pub fn ingest(
        &mut self, context:Context, sequence:u32, first_frame:u64,
        frames:u64,
    )->Result<Accepted,Reject>{
        if frames==0 || first_frame.checked_add(frames).is_none(){
            return Err(Reject::Overflow);
        }
        if let Some(previous)=self.context{
            if previous!=context{return Err(Reject::RetiredContext);}
        }
        let previous_seq=self.last_sequence;
        if let Some(last)=previous_seq {
            if sequence<=last{return Err(Reject::Late);}
            if first_frame<self.next_frame{return Err(Reject::Overlapping);}
        }
        let expected=if previous_seq.is_some(){self.next_frame}else{first_frame};
        let decision=Accepted {
            missing_sequences: previous_seq.map_or(0,|v|u64::from(sequence-v-1)),
            missing_frames: first_frame-expected,
            expected_source:expected,
            newly_anchored:previous_seq.is_none(),
        };
        self.context=Some(context);
        self.last_sequence=Some(sequence);
        self.next_frame=first_frame+frames;
        Ok(decision)
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
