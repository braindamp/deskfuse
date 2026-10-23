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

#[cfg(target_os = "android")]
mod native {
    use super::{AudioStats, RenderIdentity};
    use std::ffi::{c_char, c_int, c_void, CString};
    use std::ptr;

    const RTLD_NOW: c_int = 2;
    #[link(name = "dl")]
    unsafe extern "C" {
        fn dlopen(name: *const c_char, flags: c_int) -> *mut c_void;
        fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void;
        fn dlclose(handle: *mut c_void) -> c_int;
    }

    type AbiVersion = unsafe extern "C" fn() -> u32;
    type Open = unsafe extern "C" fn(*mut *mut c_void) -> i32;
    type Push = unsafe extern "C" fn(*mut c_void, *const i16, u32) -> i32;
    type PushIdentified = unsafe extern "C" fn(
        *mut c_void, *const i16, u32, u64, u32, u32, u32
    ) -> i32;
    type IdentityStats = unsafe extern "C" fn(*mut c_void, *mut RenderIdentity) -> i32;
    type Stats = unsafe extern "C" fn(*mut c_void, *mut AudioStats) -> i32;
    type Close = unsafe extern "C" fn(*mut c_void);
    type DiagnosticModes = unsafe extern "C" fn(*mut c_void, u32) -> i32;

    pub struct Renderer {
        library: *mut c_void,
        ctx: *mut c_void,
        push: Push,
        identified_push: PushIdentified,
        identity_stats: IdentityStats,
        stats: Stats,
        close: Close,
        configure: DiagnosticModes,
    }

    impl Renderer {
        pub fn open(path: &str) -> Result<Self, String> {
            let file = CString::new(path).map_err(|_| "invalid Android library path")?;
            let library = unsafe { dlopen(file.as_ptr(), RTLD_NOW) };
            if library.is_null() { return Err(format!("cannot load Android audio library: {path}")); }

            let functions = (|| {
                let symbol = |name: &[u8]| {
                    let ptr = unsafe { dlsym(library, name.as_ptr().cast::<c_char>()) };
                    if ptr.is_null() { Err(format!("missing native audio symbol: {}", String::from_utf8_lossy(name))) }
                    else { Ok(ptr) }
                };
                Ok::<_, String>((
                    symbol(b"df_audio_abi_version\0")?,
                    symbol(b"df_audio_open\0")?,
                    symbol(b"df_audio_push\0")?,
                    symbol(b"df_audio_stats_v2\0")?,
                    symbol(b"df_audio_close\0")?,
                    symbol(b"df_audio_set_diagnostic_modes\0")?,
                    symbol(b"df_audio_push_identified\0")?,
                    symbol(b"df_audio_identity_stats_v3\0")?,
                ))
            })();

            let (version_fn, open_fn, push_fn, stats_fn, close_fn, modes_fn,
                 identified_fn, identity_fn) = match functions {
                Ok(v) => v,
                Err(e) => {
                    unsafe { dlclose(library); }
                    return Err(e);
                }
            };
            let version: AbiVersion = unsafe { std::mem::transmute(version_fn) };
            let actual_version = unsafe { version() };
            if actual_version != 3 {
                unsafe { dlclose(library); }
                return Err(format!(
                    "Android renderer ABI mismatch: expected v3, got v{actual_version}; redeploy AAudio .so and QUIC executable together"
                ));
            }
            let open: Open = unsafe { std::mem::transmute(open_fn) };
            let push: Push = unsafe { std::mem::transmute(push_fn) };
            let stats: Stats = unsafe { std::mem::transmute(stats_fn) };
            let close: Close = unsafe { std::mem::transmute(close_fn) };
            let configure: DiagnosticModes = unsafe { std::mem::transmute(modes_fn) };
            let identified_push: PushIdentified = unsafe { std::mem::transmute(identified_fn) };
            let identity_stats: IdentityStats = unsafe { std::mem::transmute(identity_fn) };

            let mut ctx = ptr::null_mut();
            let code = unsafe { open(&mut ctx) };
            if code != 0 || ctx.is_null() {
                unsafe { dlclose(library); }
                return Err(format!("AAudio output open/start failed code={code}"));
            }
            Ok(Self { library, ctx, push, identified_push, identity_stats, stats, close, configure })
        }

        pub fn set_diagnostic_modes(&mut self, fades: bool, linear_resample: bool) -> Result<(), String> {
            let flags = u32::from(fades) | (u32::from(linear_resample) << 1);
            let code = unsafe { (self.configure)(self.ctx, flags) };
            if code == 0 { Ok(()) }
            else { Err(format!("AAudio diagnostic modes must be chosen before publishing PCM, code={code}")) }
        }

        pub fn push_pcm16(&mut self, wire_pcm: &[u8]) -> Result<bool, String> {
            if wire_pcm.len() != 960 { return Err("expected exact 240-frame stereo PCM16".into()); }
            // QUIC bytes are not guaranteed aligned for i16 reads.
            let mut native_pcm = [0i16; 480];
            for (i, sample) in native_pcm.iter_mut().enumerate() {
                *sample = i16::from_le_bytes([wire_pcm[i * 2], wire_pcm[i * 2 + 1]]);
            }
            match unsafe { (self.push)(self.ctx, native_pcm.as_ptr(), 240) } {
                0 => Ok(true),
                1 => Ok(false), // bounded queue full; never overwrite played frames
                code => Err(format!("Android AAudio queue error code={code}")),
            }
        }

        pub fn push_pcm16_identified(
            &mut self, wire_pcm: &[u8], first_source_frame: u64,
            epoch: u32, generation: u32, concealed: bool,
        ) -> Result<bool, String> {
            if wire_pcm.len() != 960 { return Err("expected exact 240-frame stereo PCM16".into()); }
            let mut native_pcm = [0i16; 480];
            for (i, sample) in native_pcm.iter_mut().enumerate() {
                *sample = i16::from_le_bytes([wire_pcm[i * 2], wire_pcm[i * 2 + 1]]);
            }
            let code = unsafe {
                (self.identified_push)(self.ctx, native_pcm.as_ptr(), 240,
                    first_source_frame, epoch, generation, u32::from(concealed))
            };
            match code {
                0 => Ok(true),
                1 => Ok(false),
                _ => Err(format!("AAudio identified PCM push failed code={code}")),
            }
        }

        pub fn identity_snapshot(&self) -> Result<RenderIdentity, String> {
            let mut s = RenderIdentity::default();
            let code = unsafe { (self.identity_stats)(self.ctx, &mut s) };
            if code == 0 { Ok(s) }
            else { Err(format!("AAudio identity stats error={code}")) }
        }

        pub fn snapshot(&self) -> Result<AudioStats, String> {
            let mut s = AudioStats::default();
            let code = unsafe { (self.stats)(self.ctx, &mut s) };
            if code == 0 { Ok(s) } else { Err(format!("AAudio stats error code={code}")) }
        }
    }

    impl Drop for Renderer {
        fn drop(&mut self) {
            unsafe {
                (self.close)(self.ctx); // ensures callback has stopped
                dlclose(self.library);
            }
        }
    }
}

#[cfg(target_os = "android")]
pub use native::Renderer as AndroidAudio;

#[cfg(not(target_os = "android"))]
pub struct AndroidAudio;

#[cfg(not(target_os = "android"))]
impl AndroidAudio {
    pub fn open(_path: &str) -> Result<Self, String> {
        Err("AAudio playback requires the Android ARM64 executable".into())
    }
    pub fn set_diagnostic_modes(&mut self, _fades: bool, _linear_resample: bool) -> Result<(), String> {
        Err("AAudio diagnostic modes require Android".into())
    }
    pub fn push_pcm16(&mut self, _bytes: &[u8]) -> Result<bool, String> {
        Err("AAudio playback requires Android".into())
    }
    pub fn push_pcm16_identified(
        &mut self, _bytes: &[u8], _source_frame: u64, _epoch: u32,
        _generation: u32, _concealed: bool
    ) -> Result<bool, String> {
        Err("AAudio source identity requires Android".into())
    }
    pub fn identity_snapshot(&self) -> Result<RenderIdentity, String> {
        Err("AAudio source identity requires Android".into())
    }
    pub fn snapshot(&self) -> Result<AudioStats, String> {
        Err("AAudio playback requires Android".into())
    }
}
