//! Bounded owner-thread startup observations; output is deferred until teardown.
use crate::android_audio::{AudioStats,PlayoutState};
use std::time::Instant;
#[derive(Debug,Clone,Copy)]
struct Sample {
    event: &'static str, us:u128, stats:Option<AudioStats>, playout:Option<PlayoutState>,
    sequence:Option<u32>, source:Option<u64>,
}
pub struct Timeline {
    start:Instant, records:[Option<Sample>;12], count:usize, active:bool,
    gate:Option<Instant>, baseline_rendered:u64, latest:Option<Sample>,
}
impl Default for Timeline {
    fn default()->Self {Self {start:Instant::now(),records:[None;12],count:0,active:false,
        gate:None,baseline_rendered:0,latest:None}}
}
impl Timeline {
    pub(crate) fn start()->Self {
        let mut value=Self {active:true,..Self::default()};
        value.mark("play_enter",None); value
    }
    fn seen(&self,event:&str)->bool {self.records[..self.count].iter().flatten().any(|s|s.event==event)}
    fn retain(&mut self,sample:Sample) {
        if self.active && self.count<self.records.len() && !self.seen(sample.event) {
            self.records[self.count]=Some(sample);self.count+=1;
        }
    }
    pub(crate) fn mark(&mut self,event:&'static str,stats:Option<AudioStats>) {
        if !self.active{return;}
        self.retain(Sample {event,us:self.start.elapsed().as_micros(),stats,playout:None,sequence:None,source:None});
    }
    pub(crate) fn opening_gate(&mut self,stats:AudioStats) {
        self.baseline_rendered=stats.rendered_frames;self.gate=Some(Instant::now());
        self.mark("gate_open_request",Some(stats));
    }
    pub fn observe(&mut self,stats:Option<AudioStats>,playout:Option<PlayoutState>,sequence:u32,source:u64) {
        if !self.active{return;}
        let sample=Sample {event:"last_observation",us:self.start.elapsed().as_micros(),stats,playout,sequence:Some(sequence),source:Some(source)};
        self.latest=Some(sample);
        self.retain(Sample {event:"first_pcm_observed",..sample});
        if stats.is_some_and(|s|s.rendered_frames>self.baseline_rendered) {
            self.retain(Sample {event:"first_consumption_observed",..sample});
        }
        if let Some(gate)=self.gate {
            if gate.elapsed().as_millis()>=100 {self.retain(Sample {event:"after_gate_100ms_observed",..sample});}
            if gate.elapsed().as_millis()>=300 {self.retain(Sample {event:"after_gate_300ms_observed",..sample});}
        }
    }
}
impl Drop for Timeline {
    fn drop(&mut self) {
        if !self.active{return;}
        if let Some(sample)=self.latest.take(){self.retain(sample);}
        #[cfg(target_os="android")]
        for sample in self.records[..self.count].iter().flatten() {
            log_event(&format!("STARTUP_TIMELINE event={} t_us={} sequence={:?} source={:?} stats={:?} playout={:?}",
                sample.event,sample.us,sample.sequence,sample.source,sample.stats,sample.playout));
        }
    }
}

#[cfg(target_os="android")]
pub fn log_event(text:&str) {
    use std::ffi::{c_char,CString};
    #[link(name="log")]
    extern "C" {fn __android_log_write(priority:i32,tag:*const c_char,text:*const c_char)->i32;}
    let bounded:String=text.chars().take(1536).collect::<String>().replace('\0',"\\0");
    if let Ok(text)=CString::new(bounded) {
        unsafe {__android_log_write(4,b"DeskFuseAudio\0".as_ptr().cast(),text.as_ptr());}
    }
}