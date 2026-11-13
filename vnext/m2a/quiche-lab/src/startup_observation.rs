//! Bounded owner-thread startup observations; output is deferred until teardown.
use crate::android_audio::{AudioStats,PlayoutState,CallbackMetricsV8};
use std::time::Instant;
// Deferred owner-thread observations only. One sample per 500-ms arrival
// bucket, bounded to the most recent twelve seconds; no callback work.
const INTERVAL_US:u128=500_000;
const INTERVAL_SLOTS:usize=24;
#[derive(Debug,Clone,Copy)]
struct Sample {
    event: &'static str, us:u128, stats:Option<AudioStats>, playout:Option<PlayoutState>,
    sequence:Option<u32>, source:Option<u64>, metrics:Option<CallbackMetricsV8>,
}
pub struct Timeline {
    start:Instant, records:[Option<Sample>;16], count:usize, active:bool,
    gate:Option<Instant>, baseline_rendered:u64, latest:Option<Sample>,
    intervals:[Option<Sample>;INTERVAL_SLOTS], interval_next:usize,
    interval_count:usize, interval_overwritten:u64, last_bucket:Option<u128>,
}
impl Default for Timeline {
    fn default()->Self {Self {start:Instant::now(),records:[None;16],count:0,active:false,
        gate:None,baseline_rendered:0,latest:None,
        intervals:[None;INTERVAL_SLOTS],interval_next:0,interval_count:0,
        interval_overwritten:0,last_bucket:None}}
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
        self.retain(Sample {event,us:self.start.elapsed().as_micros(),stats,playout:None,sequence:None,source:None,metrics:None});
    }
    pub(crate) fn opening_gate(&mut self,stats:AudioStats) {
        self.baseline_rendered=stats.rendered_frames;self.gate=Some(Instant::now());
        self.mark("gate_open_request",Some(stats));
    }
    pub fn metrics(&mut self,event:&'static str,metrics:Option<CallbackMetricsV8>) {
        if !self.active{return;}
        self.retain(Sample {event,us:self.start.elapsed().as_micros(),stats:None,playout:None,sequence:None,source:None,metrics});
    }
    pub fn observe(&mut self,stats:Option<AudioStats>,playout:Option<PlayoutState>,sequence:u32,source:u64,measure:impl FnOnce()->Option<CallbackMetricsV8>) {
        if !self.active{return;}
        let consuming=stats.is_some_and(|s|s.rendered_frames>self.baseline_rendered);
        let elapsed=self.gate.map(|gate|gate.elapsed().as_millis());
        let milestone=!self.seen("first_pcm_observed") ||
            (consuming && !self.seen("first_consumption_observed")) ||
            (elapsed.is_some_and(|v|v>=100) && !self.seen("after_gate_100ms_observed")) ||
            (elapsed.is_some_and(|v|v>=300) && !self.seen("after_gate_300ms_observed"));
        let metrics=if milestone {measure()} else {None};
        let sample=Sample {event:"last_observation",us:self.start.elapsed().as_micros(),stats,playout,sequence:Some(sequence),source:Some(source),metrics};
        self.latest=Some(sample);
        // Keep a rolling, strictly bounded view of source progress and native
        // occupancy across a sustained run. The sample precedes admission;
        // receipt never implies publication. Empty buckets have no invented data.
        if let Some(gate)=self.gate {
            let bucket=gate.elapsed().as_micros()/INTERVAL_US;
            if self.last_bucket!=Some(bucket) {
                if self.interval_count==INTERVAL_SLOTS {
                    self.interval_overwritten=self.interval_overwritten.saturating_add(1);
                } else {
                    self.interval_count+=1;
                }
                self.intervals[self.interval_next]=Some(sample);
                self.interval_next=(self.interval_next+1)%INTERVAL_SLOTS;
                self.last_bucket=Some(bucket);
            }
        }
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
            if let Some(metrics)=sample.metrics {
                log_event(&format!("CALLBACK_METRICS event={} t_us={} buckets_le_96_240_480_960_1920_above={:?} measured_calls={} total_wall_ns={} maximum_wall_ns={} clock_failures={} measurement_enabled={}",
                    sample.event,sample.us,metrics.size_buckets,metrics.measured_calls,metrics.total_wall_ns,metrics.maximum_wall_ns,metrics.clock_failures,metrics.measurement_enabled));
            }
        }
        #[cfg(target_os="android")]
        {
            log_event(&format!(
                "STEADY_INTERVALS cadence_us={} slots={} retained={} overwritten={} sampling=on_packet_arrival_before_admission",
                INTERVAL_US,INTERVAL_SLOTS,self.interval_count,self.interval_overwritten));
            let oldest=if self.interval_count==INTERVAL_SLOTS {self.interval_next} else {0};
            for n in 0..self.interval_count {
                if let Some(sample)=self.intervals[(oldest+n)%INTERVAL_SLOTS] {
                    log_event(&format!(
                        "STEADY_INTERVAL t_us={} sequence={:?} source={:?} stats={:?} playout={:?}",
                        sample.us,sample.sequence,sample.source,sample.stats,sample.playout));
                }
            }
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