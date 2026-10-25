//! Narrow, Android-only dynamic FFI to the independently built C++ AAudio
//! renderer. Rust owns network/TLS/PCM validation, C++ owns AAudio callback
//! and output-demand consumption. Library path is explicit and local.
//!
//! No async Rust runtime, user-supplied filename in a remote QUIC packet,
//! callback allocations, nor hidden audio playback on Windows CI.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct AudioStats {
    pub accepted_frames: u64,
    pub rendered_frames: u64,
    pub silence_frames: u64,
    pub overflow_packets: u64,
    pub callback_count: u64,
    pub queued_frames: u64,
    pub stream_error: i32,
    pub actual_sample_rate: i32,
    pub actual_channels: i32,
    // ABI version 2. Never read these from an old renderer shared library.
    pub drift_correction_ppm: i32,
    pub underrun_events: u64,
    pub reprime_events: u64,
}

#[repr(C)]
#[derive(Debug,Default,Clone,Copy)]
pub struct OutputStateV1 {pub size:u32,pub version:u32,pub sharing:i32,pub performance:i32,pub frames_per_burst:i32,pub usage:i32,pub format:i32,pub reserve_frames:i32}

// ABI v3: renderer-owned source position at last committed callback output,
// plus the number of physical source frames released or discarded. Snapshots
// are diagnostic until the end-to-end ownership/phase gates are met.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct RenderIdentity {
    pub last_committed_source_frame: u64,
    pub last_fraction_q32: u32,
    pub last_epoch: u32,
    pub last_generation: u32,
    pub reserved: u32,
    pub released_source_frames: u64,
    pub discarded_on_rebuffer_frames: u64,
    pub concealed_output_frames: u64,
}

// V4 exactly matches CoreSnapshot in render_core.h. V1/V2/V3 ABI
// structures remain unchanged and are never resized behind old symbols.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct RenderSnapshot {
    pub callback_seq: u64,
    pub committed_output: u64,
    pub rendered: u64,
    pub silence: u64,
    pub released: u64,
    pub discarded: u64,
    pub concealed_outputs: u64,
    pub underruns: u64,
    pub reprimes: u64,
    pub invalidations: u64,
    pub last_source: u64,
    pub last_support_source: u64,
    pub earliest_needed: u64,
    pub published_tail: u64,
    pub fraction_q32: u32,
    pub epoch: u32,
    pub stream: u32,
    pub format: u32,
    pub connection: u32,
    pub playout: u32,
    pub drift_ppm: i32,
    pub reserved: u32,
}
// ABI v5 additive off-callback Android HAL presentation measurement.
// It is not an acoustic mic timestamp and cannot be subtracted from Windows
// WASAPI QPC without independently measured cross-device clock alignment.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct PresentationTimeV5 {
    pub output_frame_position: i64,
    pub monotonic_nanoseconds: i64,
    pub aaudio_result: i32,
    pub reserved: i32,
}
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct PlayoutState {
    pub queued_playable_frames: u64,
    pub current_source_frame: u64,
    pub requested_recovery: u64,
    pub applied_recovery: u64,
    pub primed: u32,
    pub last_callback_frames: u32,
    pub max_callback_frames: u32,
    pub playout_generation: u32,
    pub stream_error: i32,
    pub fraction_q32: u32,
    pub source_valid: u32,
    pub xrun_count: i32,
    pub clipped_samples: u64,
}
// Additive v7 timing capability; old v5 timestamp layout is unchanged.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct TimingSampleV7 {
    pub size:u32, pub version:u32, pub output_generation:u64,
    pub before_ns:i64, pub after_ns:i64,
    pub written_before:i64, pub written_after:i64,
    pub timestamp_frame:i64, pub timestamp_ns:i64,
    pub clock_result:i32, pub timestamp_result:i32,
    pub sample_rate:i32, pub state_before:i32, pub state_after:i32,
    pub xrun_count:i32, pub buffer_frames:i32, pub capacity_frames:i32,
    pub device_id:i32, pub reserved:i32,
}
// V7 adds pre-start buffer policy; all historical stats layouts stay fixed.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct OutputConfigV7 {
    pub size:u32, pub version:u32, pub buffer_bursts:u32, pub reserved:u32,
}
impl OutputConfigV7 {
    pub fn new(buffer_bursts:u32)->Result<Self,String> {
        if !matches!(buffer_bursts,0|2|3) {return Err("buffer bursts must be 0, 2 or 3".into())}
        Ok(Self {size:16,version:7,buffer_bursts,reserved:0})
    }
}
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct OutputConfigV8 {
    pub size:u32, pub version:u32, pub buffer_bursts:u32,
    pub sharing_policy:u32, pub usage_policy:u32, pub reserve_frames:u32,
    pub reserved0:u32, pub reserved1:u32,
}
impl OutputConfigV8 {
    pub fn new(buffer_bursts:u32,sharing_policy:u32,usage_policy:u32,reserve_frames:u32)->Result<Self,String>{
        OutputConfigV7::new(buffer_bursts)?;
        if sharing_policy>1 || usage_policy>1 || !matches!(reserve_frames,1440|1200|960|720|480){
            return Err("unsupported sharing, usage or native reserve".into());
        }
        Ok(Self{size:32,version:8,buffer_bursts,sharing_policy,usage_policy,reserve_frames,reserved0:0,reserved1:0})
    }
}
impl Default for OutputConfigV8 {
    fn default()->Self {Self::new(0,0,0,1440).expect("constant reference config")}
}
pub fn validate_abi_v8(actual:u32)->Result<(),String>{
    if actual==8 {Ok(())} else {Err(format!("AAudio ABI mismatch expected v8, got v{actual}: redeploy both Android binaries"))}
}
#[repr(C)]
#[derive(Debug,Default,Clone,Copy)]
pub struct CallbackMetricsV8 {
    pub size_buckets:[u64;6],
    pub measured_calls:u64,pub total_wall_ns:u64,pub maximum_wall_ns:u64,pub clock_failures:u64,
    pub measurement_enabled:u32,pub reserved:u32,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Admission {
    Accepted,
    Overflow,
    RecoverPending,
    Discontinuity,
    Stale,
}
pub fn validate_abi_v7(actual: u32) -> Result<(), String> {
    if actual == 7 { Ok(()) }
    else {
        Err(format!(
            "AAudio ABI mismatch expected v7, got v{actual}: redeploy both Android binaries"
        ))
    }
}

#[cfg(test)]
mod abi_tests {
    use super::*;
    #[test]
    fn experiment_config_is_validated_and_v8_is_fail_closed(){
        assert_eq!(std::mem::size_of::<OutputConfigV8>(),32);
        assert_eq!(std::mem::size_of::<CallbackMetricsV8>(),88);
        assert_eq!(std::mem::offset_of!(OutputConfigV8,reserve_frames),20);
        for r in [1440,1200,960,720,480] {assert!(OutputConfigV8::new(0,1,1,r).is_ok());}
        for (b,s,u,r) in [(1,0,0,1440),(0,2,0,1440),(0,0,2,1440),(0,0,0,479)] {
            assert!(OutputConfigV8::new(b,s,u,r).is_err());
        }
        for v in [0,7,9,u32::MAX] {assert!(validate_abi_v8(v).is_err());}
        assert!(validate_abi_v8(8).is_ok());
    }
    #[test]
    fn reject_mismatched_library_versions_before_struct_io() {
        for version in [0, 1, 2, 3, 4, 5, 6, 8, u32::MAX] {
            assert!(validate_abi_v7(version).is_err());
        }
        assert!(validate_abi_v7(7).is_ok());
    }
    #[test]
    fn historical_struct_prefixes_and_new_coherent_record_size_remain_exact() {
        assert_eq!(std::mem::size_of::<TimingSampleV7>(),104);
        assert_eq!(std::mem::offset_of!(TimingSampleV7,timestamp_frame),48);
        assert_eq!(std::mem::offset_of!(TimingSampleV7,clock_result),64);
        assert_eq!(std::mem::size_of::<OutputConfigV7>(),16);
        assert_eq!(std::mem::offset_of!(OutputConfigV7,buffer_bursts),8);
        assert!(OutputConfigV7::new(1).is_err());
        for policy in [0,2,3] {assert_eq!(OutputConfigV7::new(policy).unwrap().buffer_bursts,policy);}
        assert_eq!(std::mem::size_of::<AudioStats>(),80);
        assert_eq!(std::mem::size_of::<RenderIdentity>(),48);
        assert_eq!(std::mem::size_of::<RenderSnapshot>(),144);
        assert_eq!(std::mem::size_of::<PresentationTimeV5>(),24);
        assert_eq!(std::mem::size_of::<PlayoutState>(),72);
    }
}

#[cfg(target_os = "android")]
mod native {
    use super::{TimingSampleV7, OutputConfigV8, CallbackMetricsV8, Admission, AudioStats, RenderIdentity, RenderSnapshot, PresentationTimeV5, PlayoutState};
    use std::ffi::{c_char, c_int, c_void, CString};
    use std::ptr;

    const RTLD_NOW: c_int = 2;
    #[link(name="dl")]
    unsafe extern "C" {
        fn dlopen(path: *const c_char, flags:c_int) -> *mut c_void;
        fn dlsym(handle:*mut c_void, sym:*const c_char) -> *mut c_void;
        fn dlclose(handle:*mut c_void) -> c_int;
    }
    type Abi=unsafe extern "C" fn() -> u32;
    type Open=unsafe extern "C" fn(*const OutputConfigV8,*mut *mut c_void)->i32;
    type Unsupported=unsafe extern "C" fn(*mut c_void)->u32;
    type CallbackMetrics=unsafe extern "C" fn(*mut c_void,*mut CallbackMetricsV8)->i32;
    type Close=unsafe extern "C" fn(*mut c_void);
    type Modes=unsafe extern "C" fn(*mut c_void,u32)->i32;
    type PushV5=unsafe extern "C" fn(
        *mut c_void,*const i16,u32,u64,u32,u32,u32,u32,u32,u64)->i32;
    type Stats=unsafe extern "C" fn(*mut c_void,*mut AudioStats)->i32;
    type IdentityStats=unsafe extern "C" fn(*mut c_void,*mut RenderIdentity)->i32;
    type Request=unsafe extern "C" fn(*mut c_void)->u64;
    type Applied=unsafe extern "C" fn(*mut c_void)->u64;
    type Snapshot=unsafe extern "C" fn(
        *mut c_void,*mut RenderSnapshot,*mut u64)->i32;
    type Presentation=unsafe extern "C" fn(
        *mut c_void,*mut PresentationTimeV5)->i32;
    type Timing=unsafe extern "C" fn(*mut c_void,*mut TimingSampleV7)->i32;
    type Playout=unsafe extern "C" fn(*mut c_void,*mut PlayoutState)->i32;

    pub struct Renderer {
        library:*mut c_void, ctx:*mut c_void,
        push:PushV5, stats:Stats, identity_stats:IdentityStats,
        modes:Modes, close:Close, request:Request, applied:Applied,
        snapshot:Snapshot, presentation:Presentation, playout:Playout, timing:Timing, unsupported:Unsupported,
        callback_metrics:CallbackMetrics,
        output:Option<super::OutputStateV1>,
    }
    impl Renderer {
        pub fn open(path:&str)->Result<Self,String> {Self::open_with_buffer(path,0)}
        pub fn open_with_buffer(path:&str,buffer_bursts:u32)->Result<Self,String> {
            Self::open_with_config(path,OutputConfigV8::new(buffer_bursts,0,0,1440)?)
        }
        pub fn open_with_config(path:&str,output_config:OutputConfigV8)->Result<Self,String> {
            let path_c=CString::new(path).map_err(|_|"invalid library path")?;
            let library=unsafe{dlopen(path_c.as_ptr(),RTLD_NOW)};
            if library.is_null() {return Err(format!("cannot load Android audio library: {path}"));}
            let sym=|name:&[u8]|->Result<*mut c_void,String>{
                let p=unsafe{dlsym(library,name.as_ptr().cast::<c_char>())};
                if p.is_null(){Err(format!("missing Android symbol {}",String::from_utf8_lossy(name)))}
                else{Ok(p)}
            };
            // Reject mismatched library BEFORE requesting or calling any
            // symbols with evolving structure layouts.
            let abi=match sym(b"df_audio_abi_version\0"){
                Ok(p)=>unsafe{std::mem::transmute::<*mut c_void,Abi>(p)},
                Err(e)=>{unsafe{dlclose(library)};return Err(e);}
            };
            let actual=unsafe{abi()};
            if let Err(error)=super::validate_abi_v8(actual) {
                unsafe{dlclose(library)};
                return Err(error);
            }
            let funcs=(||Ok::<_,String>((
                sym(b"df_audio_open_config_v8\0")?,
                sym(b"df_audio_close\0")?,
                sym(b"df_audio_set_diagnostic_modes\0")?,
                sym(b"df_audio_push_guarded_v5\0")?,
                sym(b"df_audio_stats_v2\0")?,
                sym(b"df_audio_identity_stats_v3\0")?,
                sym(b"df_audio_request_recovery_v4\0")?,
                sym(b"df_audio_recovery_applied_v4\0")?,
                sym(b"df_audio_snapshot_v4\0")?,
                sym(b"df_audio_presentation_time_v5\0")?,
                sym(b"df_audio_playout_state_v6\0")?,
                sym(b"df_audio_timing_sample_v7\0")?,
                sym(b"df_audio_unsupported_demand_v8\0")?,
                sym(b"df_audio_callback_metrics_v8\0")?,
            )))();
            let (open,close,modes,push,stats,identity_stats,request,applied,snapshot,presentation,playout,timing,unsupported,callback_metrics)=match funcs{
                Ok(parts)=>parts,
                Err(e)=>{unsafe{dlclose(library)};return Err(e);}
            };
            let open:Open=unsafe{std::mem::transmute(open)};
            let mut ctx=ptr::null_mut();
            let code=unsafe{open(&output_config,&mut ctx)};
            if code!=0||ctx.is_null(){
                unsafe{dlclose(library)};
                return Err(format!("AAudio open error={code}"));
            }
            let output=sym(b"df_audio_output_state_v1\0").ok().and_then(|p|{
                let get:unsafe extern "C" fn(*mut c_void,*mut super::OutputStateV1)->i32=unsafe{std::mem::transmute(p)};
                let mut value=super::OutputStateV1{size:32,version:1,..Default::default()};
                if unsafe{get(ctx,&mut value)}==0 {Some(value)}else{None}
            });
            Ok(Self{
                library,ctx,output,
                close:unsafe{std::mem::transmute(close)},
                modes:unsafe{std::mem::transmute(modes)},
                push:unsafe{std::mem::transmute(push)},
                stats:unsafe{std::mem::transmute(stats)},
                identity_stats:unsafe{std::mem::transmute(identity_stats)},
                request:unsafe{std::mem::transmute(request)},
                applied:unsafe{std::mem::transmute(applied)},
                snapshot:unsafe{std::mem::transmute(snapshot)},
                presentation:unsafe{std::mem::transmute(presentation)},
                playout:unsafe{std::mem::transmute(playout)},
                timing:unsafe{std::mem::transmute(timing)},
                unsupported:unsafe{std::mem::transmute(unsupported)},
                callback_metrics:unsafe{std::mem::transmute(callback_metrics)},
            })
        }
        pub fn set_diagnostic_modes(&mut self,fades:bool,linear:bool)->Result<(),String>{
            let flags=u32::from(fades)|(u32::from(linear)<<1);
            let code=unsafe{(self.modes)(self.ctx,flags)};
            if code!=0{return Err(format!("cannot configure renderer before first PCM: code={code}"));}
            Ok(())
        }
        pub fn push_media(
            &mut self,bytes:&[u8],source:u64,epoch:u32,stream:u32,
            format:u32,connection:u32,concealed:bool,authorized_recovery:u64
        )->Result<Admission,String>{
            if bytes.len()!=960 {return Err("expected 240-frame PCM16 block".into());}
            let mut pcm=[0i16;480];
            for(i,v)in pcm.iter_mut().enumerate(){
                *v=i16::from_le_bytes([bytes[2*i],bytes[2*i+1]]);
            }
            let code=unsafe{(self.push)(self.ctx,pcm.as_ptr(),240,source,epoch,
                stream,format,connection,u32::from(concealed),authorized_recovery)};
            match code {
                0=>Ok(Admission::Accepted),
                1=>Ok(Admission::Overflow),
                -3=>Ok(Admission::RecoverPending),
                -4=>Ok(Admission::Discontinuity),
                -5=>Ok(Admission::Stale),
                x=>Err(format!("invalid native PCM publication code={x}")),
            }
        }
        pub fn request_recovery(&mut self)->u64{unsafe{(self.request)(self.ctx)}}
        pub fn recovery_applied(&self)->u64{unsafe{(self.applied)(self.ctx)}}
        pub fn pop_snapshot(&self)->Result<Option<(RenderSnapshot,u64)>,String>{
            let mut value=RenderSnapshot::default();
            let mut dropped=0;
            match unsafe{(self.snapshot)(self.ctx,&mut value,&mut dropped)}{
                1=>Ok(Some((value,dropped))),
                0=>Ok(None),
                x=>Err(format!("AAudio coherent snapshot error={x}")),
            }
        }
        pub fn output_state(&self)->Option<super::OutputStateV1>{self.output}
        pub fn timing_sample(&self)->Result<TimingSampleV7,String>{
            let mut value=TimingSampleV7 {size:104,version:7,..TimingSampleV7::default()};
            let code=unsafe{(self.timing)(self.ctx,&mut value)};
            if code==0 {Ok(value)} else {Err(format!("AAudio timing interface code={code}"))}
        }
        pub fn presentation_time(&self)->Result<PresentationTimeV5,String>{
            let mut value=PresentationTimeV5::default();
            let code=unsafe{(self.presentation)(self.ctx,&mut value)};
            if code==0{Ok(value)}else{Err(format!("AAudio HAL timestamp interface code={code}"))}
        }
        pub fn playout_state(&self)->Result<Option<PlayoutState>,String>{
            let mut value=PlayoutState::default();
            match unsafe{(self.playout)(self.ctx,&mut value)} {
                1=>Ok(Some(value)),
                0=>Ok(None),
                x=>Err(format!("AAudio playout state error={x}")),
            }
        }
        pub fn snapshot(&self)->Result<AudioStats,String>{
            let demand=unsafe{(self.unsupported)(self.ctx)};
            if demand!=0 {return Err(format!("unsupported callback demand={demand} for selected reserve; experiment failed"));}
            let mut value=AudioStats::default();
            let code=unsafe{(self.stats)(self.ctx,&mut value)};
            if code==0{Ok(value)}else{Err(format!("AAudio stats code={code}"))}
        }
        pub fn callback_metrics(&self)->Result<CallbackMetricsV8,String>{
            let mut value=CallbackMetricsV8::default();
            let code=unsafe{(self.callback_metrics)(self.ctx,&mut value)};
            if code==0{Ok(value)}else{Err(format!("callback metrics error={code}"))}
        }
        pub fn identity_snapshot(&self)->Result<RenderIdentity,String>{
            let mut value=RenderIdentity::default();
            let code=unsafe{(self.identity_stats)(self.ctx,&mut value)};
            if code==0{Ok(value)}else{Err(format!("AAudio identity stats code={code}"))}
        }
    }
    impl Drop for Renderer{
        fn drop(&mut self){unsafe{(self.close)(self.ctx);dlclose(self.library);}}
    }
}
#[cfg(target_os="android")]
pub use native::Renderer as AndroidAudio;
#[cfg(not(target_os="android"))]
pub struct AndroidAudio;
#[cfg(not(target_os="android"))]
impl AndroidAudio{
    pub fn output_state(&self)->Option<OutputStateV1>{None}
    pub fn open(_p:&str)->Result<Self,String>{Err("Android AAudio required".into())}
    pub fn open_with_buffer(_p:&str,_b:u32)->Result<Self,String>{Err("Android AAudio required".into())}
    pub fn open_with_config(_p:&str,_c:OutputConfigV8)->Result<Self,String>{Err("Android AAudio required".into())}
    pub fn callback_metrics(&self)->Result<CallbackMetricsV8,String>{Err("Android AAudio required".into())}
    pub fn set_diagnostic_modes(&mut self,_f:bool,_r:bool)->Result<(),String>{
        Err("Android AAudio required".into())
    }
    pub fn push_media(&mut self,_bytes:&[u8],_source:u64,_epoch:u32,
                      _stream:u32,_format:u32,_connection:u32,
                      _concealed:bool,_authorized_recovery:u64)->Result<Admission,String>{
        Err("Android AAudio required".into())
    }
    pub fn request_recovery(&mut self)->u64{0}
    pub fn recovery_applied(&self)->u64{0}
    pub fn pop_snapshot(&self)->Result<Option<(RenderSnapshot,u64)>,String>{
        Err("Android AAudio required".into())
    }
    pub fn timing_sample(&self)->Result<TimingSampleV7,String>{
        Err("Android AAudio required".into())
    }
    pub fn presentation_time(&self)->Result<PresentationTimeV5,String>{
        Err("Android AAudio required".into())
    }
    pub fn playout_state(&self)->Result<Option<PlayoutState>,String>{
        Err("Android AAudio required".into())
    }
    pub fn snapshot(&self)->Result<AudioStats,String>{Err("Android AAudio required".into())}
    pub fn identity_snapshot(&self)->Result<RenderIdentity,String>{
        Err("Android AAudio required".into())
    }
}
