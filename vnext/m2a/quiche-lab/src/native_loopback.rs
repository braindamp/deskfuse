//! In-process WASAPI adapter for the protected worker. One native SPSC queue;
//! PCM is borrowed through a closure, then consumed BEFORE returning to QUIC.
//! Integration stays disabled until N2/N3/N4 authority and rundown are complete.
use std::{ffi::c_void, io, mem::{size_of, transmute}, os::windows::ffi::OsStrExt, ptr};
use windows_sys::Win32::{Foundation::*, System::LibraryLoader::*};
use crate::wasapi_pipe::BorrowedChunk;
const ABI:u32=1;
pub const READY:u32=1; pub const TERMINAL:u32=2; pub const FAILED:u32=4;
pub const STOP_UNCONFIRMED:u32=64; pub const HAS_RECORDS:u32=128;
#[repr(C)]
struct Options {size:u32,abi:u32,endpoint:*const u16,source:[u8;16],build:u64,cutoff:u64,minimum:u32,reserved:u32}
#[repr(C)]
struct Opened {size:u32,abi:u32,owner:*mut c_void,data:HANDLE,ready:HANDLE,terminal:HANDLE}
#[repr(C)]
struct Record {size:u32,abi:u32,token:u64,frame:u64,qpc:u64,flags:u32,frames:u32,offset:u32,bytes:u32,pcm:*const u8}
#[repr(C)] #[derive(Default,Debug)]
pub struct Status {
    size:u32,abi:u32,pub flags:u32,pub error:i32,pub dropped:u64,
    pub period_frames:u32,pub capacity_frames:u32,pub source:[u8;16],pub driver_build:u64,
}
type Open=unsafe extern "C" fn(*const Options,*mut Opened)->i32;
type Borrow=unsafe extern "C" fn(*mut c_void,*mut Record)->i32;
type Consume=unsafe extern "C" fn(*mut c_void,u64)->i32;
type Stop=unsafe extern "C" fn(*mut c_void)->i32;
type Join=unsafe extern "C" fn(*mut c_void,u32)->i32;
type Destroy=unsafe extern "C" fn(*mut c_void)->i32;
type GetStatus=unsafe extern "C" fn(*mut c_void,*mut Status)->i32;
struct Api {module:HMODULE,open:Open,borrow:Borrow,consume:Consume,stop:Stop,join:Join,destroy:Destroy,status:GetStatus}
impl Drop for Api {fn drop(&mut self){if !self.module.is_null(){unsafe{FreeLibrary(self.module);}}}}
fn invalid(message:&str)->io::Error {io::Error::new(io::ErrorKind::InvalidData,message)}
impl Api {
    fn load()->io::Result<Self> {
        // Only the fixed approved sibling in the retained installed image closure.
        let exe=std::env::current_exe()?;
        let dll=exe.parent().ok_or_else(||invalid("worker directory missing"))?.join("deskfuse-source-control.dll");
        Self::load_path(&dll)
    }
    fn load_path(dll:&std::path::Path)->io::Result<Self> {
        let path:Vec<u16>=dll.as_os_str().encode_wide().chain(Some(0)).collect();
        unsafe {
            let module=LoadLibraryExW(path.as_ptr(),ptr::null_mut(),LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
            if module.is_null(){return Err(io::Error::last_os_error())}
            macro_rules! symbol {($name:literal,$kind:ty)=>{{
                match GetProcAddress(module,concat!($name,"\0").as_ptr()) {
                    Some(value)=>transmute::<unsafe extern "system" fn()->isize,$kind>(value),
                    None=>{FreeLibrary(module);return Err(invalid("owned loopback ABI unavailable"));}
                }
            }}}
            Ok(Self {module,open:symbol!("deskfuse_open_owned_loopback",Open),
                borrow:symbol!("deskfuse_loopback_try_borrow",Borrow),consume:symbol!("deskfuse_loopback_consume",Consume),
                stop:symbol!("deskfuse_loopback_request_stop",Stop),join:symbol!("deskfuse_loopback_join",Join),
                destroy:symbol!("deskfuse_loopback_destroy",Destroy),status:symbol!("deskfuse_loopback_status",GetStatus)})
        }
    }
}
pub struct Source {api:Option<Api>,owner:*mut c_void,data:HANDLE,ready:HANDLE,terminal:HANDLE,quarantined:bool}
// Native COM never crosses threads; transfer once, then one Rust lifecycle/consumer owner.
unsafe impl Send for Source {}
impl Source {
    /// Caller supplies the broker-observed owned endpoint and startup deadline.
    /// This function neither provisions a device nor waits for COM/audio setup.
    pub fn open_owned(endpoint:&str,source:[u8;16],build:u64,minimum:bool,cutoff:u64)->io::Result<Self> {
        if endpoint.is_empty() || endpoint.contains('\0') || endpoint.encode_utf16().count()>1024 ||
            source==[0;16] || build!=0x2026102502 || cutoff==0 {return Err(invalid("invalid owned loopback context"))}
        let api=Api::load()?;
        let endpoint:Vec<u16>=endpoint.encode_utf16().chain(Some(0)).collect();
        let options=Options{size:size_of::<Options>() as u32,abi:ABI,endpoint:endpoint.as_ptr(),source,build,cutoff,minimum:minimum as u32,reserved:0};
        let mut opened=Opened{size:size_of::<Opened>() as u32,abi:ABI,owner:ptr::null_mut(),data:ptr::null_mut(),ready:ptr::null_mut(),terminal:ptr::null_mut()};
        if unsafe{(api.open)(&options,&mut opened)}!=0 {return Err(invalid("owned loopback open refused"))}
        let mut value=Self{api:Some(api),owner:opened.owner,data:opened.data,ready:opened.ready,terminal:opened.terminal,quarantined:false};
        if value.owner.is_null() || value.data.is_null() || value.ready.is_null() || value.terminal.is_null() {
            value.quarantined=true;return Err(invalid("owned loopback returned invalid handles"));
        }
        Ok(value)
    }
    pub fn data_event(&self)->HANDLE {self.data}
    pub fn ready_event(&self)->HANDLE {self.ready}
    pub fn terminal_event(&self)->HANDLE {self.terminal}
    pub fn status(&self)->io::Result<Status> {
        let mut result=Status{size:size_of::<Status>() as u32,abi:ABI,..Status::default()};
        let api=self.api.as_ref().ok_or_else(||invalid("loopback owner destroyed"))?;
        if unsafe{(api.status)(self.owner,&mut result)}!=0{return Err(invalid("loopback status failed"))}Ok(result)
    }
    /// The higher-ranked closure cannot return a reference into the native slot.
    /// Unwinding still consumes the borrow. No native pointer survives this call.
    pub fn with_next<R>(&mut self, use_record:impl for<'a> FnOnce(BorrowedChunk<'a>)->R)->io::Result<Option<R>> {
        if self.quarantined {return Err(invalid("loopback consumer quarantined"))}
        let api=self.api.as_ref().ok_or_else(||invalid("loopback owner destroyed"))?;
        let mut record=Record{size:size_of::<Record>() as u32,abi:ABI,token:0,frame:0,qpc:0,flags:0,frames:0,offset:0,bytes:0,pcm:ptr::null()};
        match unsafe{(api.borrow)(self.owner,&mut record)} {
            0=>return Ok(None),1=>{},_=>return Err(invalid("loopback borrow failed")),
        }
        struct Pin<'a>{owner:*mut c_void,token:u64,consume:Consume,stop:Stop,failed:&'a mut bool,active:bool}
        impl Pin<'_> {
            fn finish(&mut self) {if self.active {self.active=false;if unsafe{(self.consume)(self.owner,self.token)}!=0 {
                *self.failed=true;unsafe{(self.stop)(self.owner);}
            }}}
        }
        impl Drop for Pin<'_>{fn drop(&mut self){self.finish();}}
        let mut pin=Pin{owner:self.owner,token:record.token,consume:api.consume,stop:api.stop,failed:&mut self.quarantined,active:true};
        if record.token==0 || record.pcm.is_null() || !(1..=240).contains(&record.frames) || record.bytes!=record.frames*4 ||
            record.frame.checked_add(u64::from(record.frames)).is_none() {
            *pin.failed=true;unsafe{(pin.stop)(pin.owner);}return Err(invalid("invalid native PCM record"));
        }
        // ABI guarantees immutable, initialized bytes pinned until exact consume.
        let pcm=unsafe{std::slice::from_raw_parts(record.pcm,record.bytes as usize)};
        let result=use_record(BorrowedChunk{device_frame:record.frame,qpc_100ns:record.qpc,flags:record.flags,
            original_packet_offset_frames:record.offset,frames:record.frames as usize,pcm});
        pin.finish();
        if *pin.failed {return Err(invalid("loopback consume unconfirmed; custody retained"))}
        Ok(Some(result))
    }
    pub fn request_stop(&self)->io::Result<()> {
        let api=self.api.as_ref().ok_or_else(||invalid("loopback owner destroyed"))?;
        if unsafe{(api.stop)(self.owner)}!=0{return Err(invalid("loopback Stop request failed"))}Ok(())
    }
    /// Failure retains object AND loaded DLL for retry/quarantine/exact job termination.
    pub fn stop_and_join(&mut self,timeout_ms:u32)->io::Result<()> {
        if self.owner.is_null(){return Ok(())}
        self.request_stop()?;
        let api=self.api.as_ref().unwrap();
        match unsafe{(api.join)(self.owner,timeout_ms)} {
            0=>{},1=>return Err(io::Error::new(io::ErrorKind::TimedOut,"loopback producer still running")),
            _=>return Err(invalid("loopback native rundown unconfirmed")),
        }
        if self.quarantined || unsafe{(api.destroy)(self.owner)}!=0 {return Err(invalid("loopback storage custody retained"))}
        self.owner=ptr::null_mut();self.api.take();Ok(())
    }
}
impl Drop for Source {
    fn drop(&mut self) {
        // Drop never blocks an owner turn. Normal cleanup explicitly joins first.
        if !self.owner.is_null() && self.stop_and_join(0).is_err() {
            if let Some(api)=self.api.take(){std::mem::forget(api);} // keep executable code mapped
        }
    }
}

#[cfg(test)] mod tests {
    use super::*;
    use std::sync::{Arc,atomic::{AtomicUsize,Ordering}};
    #[test] fn x64_layout_matches_native_abi() {
        assert_eq!(size_of::<Options>(),56);assert_eq!(size_of::<Opened>(),40);
        assert_eq!(size_of::<Record>(),56);assert_eq!(size_of::<Status>(),56);
        assert_eq!(std::mem::offset_of!(Record,pcm),48);
    }
    #[test] fn built_dll_exports_match_rust_without_opening_a_device() {
        let Some(path)=std::env::var_os("DESKFUSE_LOOPBACK_TEST_DLL") else {return};
        let api=Api::load_path(std::path::Path::new(&path)).unwrap();
        let endpoint=[0u16];
        let options=Options{size:56,abi:1,endpoint:endpoint.as_ptr(),source:[0;16],build:0,cutoff:0,minimum:0,reserved:0};
        let mut opened=Opened{size:40,abi:1,owner:ptr::null_mut(),data:ptr::null_mut(),ready:ptr::null_mut(),terminal:ptr::null_mut()};
        unsafe {
            assert_eq!((api.open)(&options,&mut opened),-1);assert!(opened.owner.is_null());
            assert_eq!((api.borrow)(ptr::null_mut(),ptr::null_mut()),-1);
            assert_eq!((api.consume)(ptr::null_mut(),1),-1);assert_eq!((api.stop)(ptr::null_mut()),-1);
            assert_eq!((api.join)(ptr::null_mut(),0),-1);assert_eq!((api.destroy)(ptr::null_mut()),-1);
            assert_eq!((api.status)(ptr::null_mut(),ptr::null_mut()),-1);
        }
    }
    struct Fake {pcm:[u8;960],borrowed:bool,fail_consume:bool,consumed:Arc<AtomicUsize>,destroyed:Arc<AtomicUsize>}
    unsafe extern "C" fn no_open(_: *const Options,_:*mut Opened)->i32 {-1}
    unsafe extern "C" fn borrow(value:*mut c_void,out:*mut Record)->i32 {
        let fake=&mut *value.cast::<Fake>();if fake.borrowed{return -1}fake.borrowed=true;
        *out=Record{size:56,abi:1,token:9,frame:480,qpc:0,flags:4,frames:240,offset:240,bytes:960,pcm:fake.pcm.as_ptr()};1
    }
    unsafe extern "C" fn consume(value:*mut c_void,token:u64)->i32 {
        let fake=&mut *value.cast::<Fake>();
        if fake.fail_consume || !fake.borrowed || token!=9{return -1}
        fake.borrowed=false;fake.consumed.fetch_add(1,Ordering::SeqCst);0
    }
    unsafe extern "C" fn stop(_:*mut c_void)->i32 {0}
    unsafe extern "C" fn join(_:*mut c_void,_:u32)->i32 {0}
    unsafe extern "C" fn destroy(value:*mut c_void)->i32 {
        if (*value.cast::<Fake>()).borrowed{return -1}
        let fake=Box::from_raw(value.cast::<Fake>());fake.destroyed.fetch_add(1,Ordering::SeqCst);0
    }
    unsafe extern "C" fn status(_:*mut c_void,_:*mut Status)->i32 {-1}
    fn fake_source(fail_consume:bool)->(Source,Arc<AtomicUsize>,Arc<AtomicUsize>) {
        let consumed=Arc::new(AtomicUsize::new(0));let destroyed=Arc::new(AtomicUsize::new(0));
        let owner=Box::into_raw(Box::new(Fake{pcm:[0x37;960],borrowed:false,fail_consume,
            consumed:consumed.clone(),destroyed:destroyed.clone()})).cast();
        (Source{api:Some(Api{module:ptr::null_mut(),open:no_open,borrow,consume,stop,join,destroy,status}),
            owner,data:ptr::null_mut(),ready:ptr::null_mut(),terminal:ptr::null_mut(),quarantined:false},consumed,destroyed)
    }
    #[test] fn borrow_is_consumed_before_return_and_during_unwind_but_failed_consume_preserves_custody() {
        let (mut source,consumed,destroyed)=fake_source(false);
        let mut assembler=crate::wasapi_pipe::Assembler::default();
        let complete=source.with_next(|record|assembler.push_borrowed(record)).unwrap().unwrap().unwrap();
        assert_eq!(complete.pcm,[0x37;960]);assert_eq!(complete.first_qpc_100ns,0);
        assert_eq!(consumed.load(Ordering::SeqCst),1);
        let unwind=std::panic::catch_unwind(std::panic::AssertUnwindSafe(||{
            let _=source.with_next::<()>(|_|panic!("synthetic consumer panic"));
        }));
        assert!(unwind.is_err());assert_eq!(consumed.load(Ordering::SeqCst),2);
        source.stop_and_join(0).unwrap();assert_eq!(destroyed.load(Ordering::SeqCst),1);
        let (mut source,_,destroyed)=fake_source(true);
        assert!(source.with_next(|_|()).is_err());assert!(source.stop_and_join(0).is_err());
        assert!(!source.owner.is_null());assert!(source.api.is_some());assert_eq!(destroyed.load(Ordering::SeqCst),0);
        // Test-only cleanup of synthetic fault; no such recovery exists in production.
        unsafe {drop(Box::from_raw(source.owner.cast::<Fake>()));}source.owner=ptr::null_mut();
    }
}
