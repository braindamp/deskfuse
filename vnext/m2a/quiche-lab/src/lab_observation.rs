//! One bounded copy of observations already owned by the Rust receiver.
//! No callback queue reader, PCM ownership or transmission timer lives here.
use std::sync::{Mutex,atomic::{AtomicU64,AtomicBool,Ordering}};
use crate::android_audio::{AudioStats,PlayoutState,TimingSampleV7,RenderSnapshot,OutputStateV1};

#[derive(Clone,Copy,Default)]
pub struct Sample {
    pub audio:AudioStats,pub playout:Option<PlayoutState>,pub timing:Option<TimingSampleV7>,
    pub render:Option<RenderSnapshot>,pub output:Option<OutputStateV1>,
    pub received:u64,pub gaps:u64,pub late:u64,pub recovery:u64,pub missing_frames:u64,
}
struct Cell {owner:[u8;32],generation:u64,sequence:u64,sample:Option<Sample>,complete:bool}
static CELL:Mutex<Cell>=Mutex::new(Cell{owner:[0;32],generation:0,sequence:0,sample:None,complete:false});
static NEXT:AtomicU64=AtomicU64::new(1);
static ENABLED:AtomicBool=AtomicBool::new(false);
fn identity(text:&str)->Option<[u8;32]> {
    if text.len()!=64{return None}
    let mut id=[0;32];for(i,pair)in text.as_bytes().chunks_exact(2).enumerate(){
        id[i]=(pair[0]as char).to_digit(16)? as u8*16+(pair[1]as char).to_digit(16)? as u8;
    }Some(id)
}
pub fn reset(){ENABLED.store(false,Ordering::Release);if let Ok(mut c)=CELL.lock(){*c=Cell{owner:[0;32],generation:0,sequence:0,sample:None,complete:false};}}
pub fn begin(owner:&str)->Result<(),&'static str> {
    let owner=identity(owner).ok_or("invalid observation owner")?;
    let mut cell=CELL.lock().map_err(|_|"observation store poisoned")?;
    *cell=Cell{owner,generation:NEXT.fetch_add(1,Ordering::Relaxed),sequence:0,sample:None,complete:false};ENABLED.store(true,Ordering::Release);Ok(())
}
pub fn publish(sample:Sample) {
    // QUIC owner never waits for JNI/control observation. A busy reader simply
    // misses this update; the next owner health pass can publish another one.
    if let Ok(mut cell)=CELL.try_lock(){if cell.generation!=0&&!cell.complete{
        cell.sequence=cell.sequence.saturating_add(1);cell.sample=Some(sample);
    }}
}
pub fn finish(){ENABLED.store(false,Ordering::Release);if let Ok(mut cell)=CELL.lock(){cell.complete=true;}}
pub fn enabled()->bool {ENABLED.load(Ordering::Acquire)}
pub fn json(owner:&str)->Option<Vec<u8>> {
    let owner=identity(owner)?;
    let (generation,sequence,s,complete)={let cell=CELL.try_lock().ok()?;
        if cell.owner!=owner{return None}(cell.generation,cell.sequence,cell.sample?,cell.complete)};
    let a=s.audio;let p=s.playout;let t=s.timing;let o=s.output;let r=s.render.filter(|r|p.is_some_and(|p|r.playout==p.playout_generation));
    // Serialization/allocations occur only on the JNI caller, never publisher.
    serde_json::to_vec(&serde_json::json!({
        "version":1,"owner_generation":generation,"sample_seq":sequence,"complete":complete,
        "sample_mono_ns":t.filter(|x|x.clock_result==0).map(|x|x.after_ns),
        "accepted_frames":a.accepted_frames,"rendered_frames":a.rendered_frames,"silence_frames":a.silence_frames,
        "callback_count":a.callback_count,"overflow_packets":a.overflow_packets,"queued_frames":a.queued_frames,
        "underrun_events":a.underrun_events,"reprime_events":a.reprime_events,"stream_error":a.stream_error,
        "sample_rate":a.actual_sample_rate,"channels":a.actual_channels,"drift_ppm":a.drift_correction_ppm,
        "xrun_count":p.filter(|x|x.xrun_count>=0).map(|x|x.xrun_count),"clipped_samples":p.map(|x|x.clipped_samples),
        "queued_playable_frames":p.map(|x|x.queued_playable_frames),"primed":p.map(|x|x.primed),
        "playout_generation":p.map(|x|x.playout_generation),"current_source_frame":p.filter(|x|x.source_valid!=0).map(|x|x.current_source_frame),
        "source_valid":p.map(|x|x.source_valid),"last_callback_frames":p.map(|x|x.last_callback_frames),
        "requested_recovery":p.map(|x|x.requested_recovery),"applied_recovery":p.map(|x|x.applied_recovery),
        "received_packets":s.received,"source_gaps":s.gaps,"late_packets":s.late,
        "recoveries":s.recovery,"source_missing_frames":s.missing_frames,
        "output_generation":t.map(|x|x.output_generation),"clock_result":t.map(|x|x.clock_result),
        "timestamp_result":t.map(|x|x.timestamp_result),"timestamp_frame":t.filter(|x|x.timestamp_result==0&&x.timestamp_frame>=0).map(|x|x.timestamp_frame),
        "timestamp_ns":t.filter(|x|x.timestamp_result==0&&x.timestamp_ns>=0).map(|x|x.timestamp_ns),"written_before":t.filter(|x|x.written_before>=0).map(|x|x.written_before),"written_after":t.filter(|x|x.written_after>=0).map(|x|x.written_after),
        "buffer_frames":t.map(|x|x.buffer_frames),"capacity_frames":t.map(|x|x.capacity_frames),"device":t.map(|x|x.device_id),
        "sharing":o.map(|x|x.sharing),"performance":o.map(|x|x.performance),"frames_per_burst":o.map(|x|x.frames_per_burst),
        "actual_usage":o.map(|x|x.usage),"format":o.map(|x|x.format),"reserve_frames":o.map(|x|x.reserve_frames),
        "render_epoch":r.map(|x|x.epoch),"render_stream":r.map(|x|x.stream),"render_connection":r.map(|x|x.connection),
        "callback_seq":r.map(|x|x.callback_seq),"discarded_frames":r.map(|x|x.discarded),"concealed_frames":r.map(|x|x.concealed_outputs)
    })).ok()
}

#[cfg(test)]mod tests {
    use super::*;
    #[test]fn snapshots_are_owner_fenced_and_reset_without_pcm_or_queue_consumption(){
        let a="11".repeat(32);let b="22".repeat(32);begin(&a).unwrap();assert!(json(&a).is_none());
        publish(Sample{audio:AudioStats{rendered_frames:240,..Default::default()},..Default::default()});
        assert!(json(&b).is_none());let v:serde_json::Value=serde_json::from_slice(&json(&a).unwrap()).unwrap();
        assert_eq!(v["rendered_frames"],240);assert!(v["xrun_count"].is_null());finish();
        begin(&b).unwrap();assert!(json(&a).is_none());assert!(json(&b).is_none());finish();
    }
}
