//! Thread-owned numeric diagnostics. Recording never formats, allocates,
//! waits, or touches a writer. Only the quiescent owner calls `flush`.
use crate::source_age::AgeDecision;
use std::io::{self, Write};

#[derive(Clone, Copy, Debug)]
pub enum FilterStage {
    ReaderResidency,
    InitialMissing,
    InitialFuture,
    InitialExpired,
    AssembledMissing,
    AssembledFuture,
    AssembledExpired,
    AfterSequence,
    AdmitExpired,
}
impl FilterStage {
    fn name(self) -> &'static str {
        match self {
            Self::ReaderResidency => "reader_residency",
            Self::InitialMissing => "initial_qpc_missingtimestamp",
            Self::InitialFuture => "initial_qpc_futuretimestamp",
            Self::InitialExpired => "initial_qpc_expired",
            Self::AssembledMissing => "assembled_qpc_missingtimestamp",
            Self::AssembledFuture => "assembled_qpc_futuretimestamp",
            Self::AssembledExpired => "assembled_qpc_expired",
            Self::AfterSequence => "after_sequence_qpc",
            Self::AdmitExpired => "quiche_admit_expired",
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub enum Event {
    Ingress {
        ordinal: u64,
        frame: u64,
        frames: u64,
        flags: u32,
        qpc: u64,
        offset: u32,
    },
    Gap {
        expected: u64,
        actual: u64,
        frames: u64,
        flags: u32,
        qpc: u64,
        count: u64,
    },
    Proven {
        frame: u64,
        offset: u32,
        qpc: u64,
        now: u64,
        saved: u64,
    },
    Pair {
        first: u64,
        second: u64,
        first_qpc: u64,
        second_qpc: u64,
        first_now: u64,
        second_now: u64,
        first_age: AgeDecision,
        second_age: AgeDecision,
        count: u64,
    },
    Filter {
        stage: FilterStage,
        reason: AgeDecision,
        sequence: Option<u32>,
        frame: u64,
        frames: u64,
        qpc: u64,
        now: u64,
        count: u64,
    },
    Assembled {
        ordinal: u64,
        frame: u64,
        frames: u64,
        qpc: u64,
        offset: u32,
    },
    Sequence {
        sequence: u32,
        frame: u64,
        frames: u64,
        qpc: u64,
    },
    Admitted {
        sequence: u32,
        frame: u64,
        qpc: u64,
        count: u64,
    },
    QueueDrop {
        sequence: u32,
        frame: u64,
        qpc: u64,
        count: u64,
        age_max: u64,
    },
    ReaderDrop {
        frame: u64,
        frames: u64,
        count: u64,
    },
}
const CATEGORIES: usize = 18;
impl Event {
    fn category(self) -> usize {
        match self {
            Self::Ingress { .. } => 0,
            Self::Proven { .. } => 1,
            Self::Assembled { .. } => 2,
            Self::Sequence { .. } => 3,
            Self::Admitted { .. } => 4,
            Self::Gap { .. } => 5,
            Self::Pair { .. } => 6,
            Self::Filter { stage, .. } => 7 + stage as usize,
            Self::QueueDrop { .. } => 16,
            Self::ReaderDrop { .. } => 17,
        }
    }
    fn write(self, out: &mut impl Write) -> io::Result<()> {
        match self {
            Self::Ingress{ordinal,frame,frames,flags,qpc,offset} => writeln!(out,"TRACE Windows SOURCE_INGRESS ordinal={ordinal} frame={frame} frames={frames} flags={flags} qpc100={qpc} original_packet_offset_frames={offset}"),
            Self::Gap{expected,actual,frames,flags,qpc,count} => writeln!(out,"TRACE Windows SOURCE_INGRESS_GAP expected={expected} actual={actual} delta={} frames={frames} flags={flags} source_qpc100={qpc} count={count}",i128::from(actual)-i128::from(expected)),
            Self::Proven{frame,offset,qpc,now,saved} => writeln!(out,"TRACE Windows QPC_PROVEN_FRAGMENT_ACCEPT frame={frame} offset_frames={offset} first_frame_qpc100={qpc} now_qpc100={now} saved={saved}"),
            Self::Pair{first,second,first_qpc,second_qpc,first_now,second_now,first_age,second_age,count} => writeln!(out,"TRACE Windows ADJACENT_240_QPC_PAIR first_frame={first} second_frame={second} first_qpc100={first_qpc} second_qpc100={second_qpc} first_now_qpc100={first_now} second_now_qpc100={second_now} first_decision={first_age:?} second_decision={second_age:?} mixed_future_pairs={count}"),
            Self::Filter{stage,reason,sequence,frame,frames,qpc,now,count} => {
                write!(out,"TRACE Windows SOURCE_FILTER_DROP stage={}",stage.name())?;
                if let Some(sequence)=sequence {write!(out," reason={reason:?} sequence={sequence}")?;}
                writeln!(out," frame={frame} frames={frames} source_qpc100={qpc} now_qpc100={now} count={count}")
            },
            Self::Assembled{ordinal,frame,frames,qpc,offset} => writeln!(out,"TRACE Windows ASSEMBLED_PCM ordinal={ordinal} frame={frame} frames={frames} source_qpc100={qpc} original_packet_offset_frames={offset}"),
            Self::Sequence{sequence,frame,frames,qpc} => writeln!(out,"TRACE Windows SEQUENCE_ASSIGNED sequence={sequence} frame={frame} frames={frames} source_qpc100={qpc}"),
            Self::Admitted{sequence,frame,qpc,count} => writeln!(out,"TRACE Windows QUIC_ADMITTED sequence={sequence} frame={frame} source_qpc100={qpc} count={count}"),
            Self::QueueDrop{sequence,frame,qpc,count,age_max} => writeln!(out,"TRACE Windows QUIC_QUEUE_DROP sequence={sequence} source_frame={frame} qpc100={qpc} count={count} age_max_100ns={age_max}"),
            Self::ReaderDrop{frame,frames,count} => writeln!(out,"TRACE Windows WASAPI_READER_QUEUE_DROP source_frame={frame} frames={frames} reader_queue_drops={count}"),
        }
    }
}

#[derive(Clone, Copy)]
pub struct Ledger {
    total: u64,
    limit: usize,
    first: [Option<Event>; 8],
    recent: [Option<Event>; 8],
    recent_count: usize,
    next: usize,
}
impl Ledger {
    pub const fn new(limit: usize) -> Self {
        assert!(limit > 0 && limit <= 8);
        Self {
            total: 0,
            limit,
            first: [None; 8],
            recent: [None; 8],
            recent_count: 0,
            next: 0,
        }
    }
    pub fn record(&mut self, event: Event) {
        if self.total < self.limit as u64 {
            self.first[self.total as usize] = Some(event)
        } else {
            self.recent[self.next] = Some(event);
            self.next = (self.next + 1) % self.limit;
            self.recent_count = (self.recent_count + 1).min(self.limit);
        }
        self.total = self.total.saturating_add(1);
    }
    pub fn total(&self) -> u64 {
        self.total
    }
    pub fn flush(&self, out: &mut impl Write, category: usize) -> io::Result<()> {
        if self.total == 0 {
            return Ok(());
        }
        let first_count = self.total.min(self.limit as u64) as usize;
        let retained = first_count + self.recent_count;
        writeln!(out,"TRACE Windows DEFERRED category={category} total={} retained={retained} omitted={} (diagnostic samples; counters remain authoritative)",self.total,self.total-retained as u64)?;
        for event in self.first[..first_count].iter().flatten() {
            event.write(out)?;
        }
        let start = if self.recent_count == self.limit {
            self.next
        } else {
            0
        };
        for i in 0..self.recent_count {
            if let Some(event) = self.recent[(start + i) % self.limit] {
                event.write(out)?;
            }
        }
        Ok(())
    }
}
pub struct WindowsTrace {
    ledgers: [Ledger; CATEGORIES],
}
impl Default for WindowsTrace {
    fn default() -> Self {
        Self {
            ledgers: std::array::from_fn(|i| Ledger::new(if i < 5 { 3 } else { 8 })),
        }
    }
}
impl WindowsTrace {
    pub fn record(&mut self, event: Event) {
        self.ledgers[event.category()].record(event)
    }
    pub fn flush(&self, out: &mut impl Write) -> io::Result<()> {
        for (i, ledger) in self.ledgers.iter().enumerate() {
            ledger.flush(out, i)?;
        }
        Ok(())
    }
}
// No hidden heap storage: full owner diagnostics must remain below 40 KiB.
const _: () = assert!(std::mem::size_of::<WindowsTrace>() <= 40 * 1024);

#[cfg(test)]
mod tests {
    use super::*;
    struct ForbiddenSink;
    impl Write for ForbiddenSink {
        fn write(&mut self, _: &[u8]) -> io::Result<usize> {
            panic!("streaming touched output")
        }
        fn flush(&mut self) -> io::Result<()> {
            panic!("streaming flushed output")
        }
    }
    #[test]
    fn recording_is_sink_independent_and_retains_first_and_latest_per_category() {
        let _blocked_sink = ForbiddenSink;
        let mut trace = WindowsTrace::default();
        for n in 0..10_000 {
            trace.record(Event::ReaderDrop {
                frame: n * 240,
                frames: 240,
                count: n + 1,
            });
            trace.record(Event::Ingress {
                ordinal: n + 1,
                frame: n * 240,
                frames: 240,
                flags: 0,
                qpc: 1,
                offset: 0,
            });
        }
        let mut out = Vec::new();
        trace.flush(&mut out).unwrap();
        let out = String::from_utf8(out).unwrap();
        assert!(out.contains("category=17 total=10000 retained=16 omitted=9984"));
        assert!(out.contains("source_frame=0 frames=240 reader_queue_drops=1"));
        assert!(out.contains("source_frame=2399760 frames=240 reader_queue_drops=10000"));
        assert!(!out.contains("reader_queue_drops=5000\n"));
        assert!(out.contains("category=0 total=10000 retained=6 omitted=9994"));
    }
    #[test]
    fn partial_recent_ring_is_ordered_and_output_errors_are_off_path() {
        let mut ledger = Ledger::new(3);
        for n in 0..5 {
            ledger.record(Event::ReaderDrop {
                frame: n,
                frames: 240,
                count: n + 1,
            });
        }
        let mut out = Vec::new();
        ledger.flush(&mut out, 17).unwrap();
        let text = String::from_utf8(out).unwrap();
        let mut last = 0;
        for n in 1..=5 {
            let at = text.find(&format!("reader_queue_drops={n}\n")).unwrap();
            assert!(at >= last);
            last = at;
        }
        struct Failed;
        impl Write for Failed {
            fn write(&mut self, _: &[u8]) -> io::Result<usize> {
                Err(io::Error::other("closed"))
            }
            fn flush(&mut self) -> io::Result<()> {
                Ok(())
            }
        }
        assert!(ledger.flush(&mut Failed, 17).is_err());
        assert_eq!(ledger.total(), 5);
    }
}
