//! The only source buffer is the immutable kernel slot plus the required
//! 240-frame wire assembler. No PCM pipe, reader queue, pacing thread or ring.
use std::{io,ffi::c_void,sync::{Arc,atomic::AtomicBool},os::windows::io::{FromRawHandle,AsRawHandle}};
use crate::{endpoint_lease::EndpointLease,source_age::CommitTiming,fresh_anchor::CommitFence};
use windows_sys::Win32::{Foundation::*,System::{Memory::*,LibraryLoader::*,IO::DeviceIoControl}};
const ABI:u32=2;const SNAPSHOT:u32=0x22e004;const ACK:u32=0x22e008;const FENCE:u32=0x22e00c;const REVOKE:u32=0x22e010;
#[repr(C)] #[derive(Clone,Copy,Default,Debug)]
struct Geometry {state:u32,packet:u32,notifications:u32,rate:u32,channels:u32,bits:u32}
impl Geometry {
    fn validate(self)->io::Result<bool> {
        match self.state {
            0=>Ok(false),
            1 if self.notifications==2 && (1..=240).contains(&self.packet) && self.rate==48000 && self.channels==2 && self.bits==16=>Ok(true),
            _=>Err(invalid("unsupported committed PCM geometry: requires 48k stereo PCM16, two notifications, packet <=240 frames")),
        }
    }
}
#[repr(C)] #[derive(Clone,Copy,Default,Debug)]
pub struct Control {
    pub size:u32,pub abi:u32,pub state:u32,pub reserved:u32,pub source:[u8;16],
    pub generation:u64,pub control_sequence:u64,pub accepted_commit:u64,pub accepted_end_frame:u64,pub dropped_frames:u64,
    pub correlation_frame:u64,pub correlation_qpc:u64,pub published:u64,pub acknowledged:u64,
    geometry:Geometry,
}
#[repr(C)] #[derive(Clone,Copy,Default)]
struct SlotHeader {ticket:u64,generation:u64,commit:u64,qpc:u64,frame:u64,frames:u32,reserved:u32}
#[repr(C)] struct Slot {header:SlotHeader,pcm:[u8;3840]}
#[repr(C)] struct Mapping {control:Control,slots:[Slot;8]}
#[repr(C)] #[derive(Default)] struct Timing {size:u32,abi:u32,build:u64,source:[u8;16],flags:u32,export_abi:u32}
pub(crate) struct OpenedPeriod { native: NativeControl, timing: Timing, frames: u32 }
impl OpenedPeriod {
    pub(crate) fn facts(&self) -> ([u8; 16], u32, u64, u32) {
        let mut source = self.timing.source;
        source[..4].reverse(); source[4..6].reverse(); source[6..8].reverse();
        (source, self.timing.abi, self.timing.build, self.frames)
    }
    #[cfg(test)]
    pub(crate) fn fixture(source: [u8; 16]) -> Self {
        unsafe extern "C" fn service(_: *mut c_void) -> i32 { 0 }
        unsafe extern "C" fn close(_: *mut c_void) {}
        Self {
            native: NativeControl { module: std::ptr::null_mut(), object: std::ptr::null_mut(),
                event: std::ptr::null_mut(), service, close },
            timing: Timing { source, abi: 1, build: 0x2026102502, ..Timing::default() },
            frames: 240,
        }
    }
}
#[repr(C)] struct Ack {generation:u64,ticket:u64,control:u64}
pub struct Block {pub first_frame:u64,pub timing:CommitTiming,pub min_commit:u64,pub generation:u64,pub pcm:[u8;960]}
struct NativeControl {
    module:HMODULE,object:*mut c_void,pub event:HANDLE,
    service:unsafe extern "C" fn(*mut c_void)->i32,close:unsafe extern "C" fn(*mut c_void),
}
impl Drop for NativeControl {fn drop(&mut self){unsafe{if !self.object.is_null(){(self.close)(self.object);}if !self.module.is_null(){FreeLibrary(self.module);}}}}
unsafe impl Send for NativeControl {} // opaque event/thread owner; no COM services cross threads
pub struct Source {
    file:std::fs::File,section:HANDLE,pub event:HANDLE,map:*const Mapping,native:NativeControl,
    lease:Option<EndpointLease>,broker_owned:bool,generation:u64,ticket:u64,offset:usize,slot:Option<SlotHeader>,
    geometry_reported:bool,have:usize,next_frame:Option<u64>,first:u64,oldest:u64,min_commit:u64,pcm:[u8;960],
}
unsafe impl Send for Source {} // transferred once from setup; used by ONE owner thereafter
fn invalid(s:&str)->io::Error {io::Error::new(io::ErrorKind::InvalidData,s)}
impl Source {
    pub fn open(peer:&str,dll:&str,minimum:bool,cancel:&AtomicBool)->io::Result<Self> {
        let lease=EndpointLease::provision(peer,cancel)?;
        let period=Self::open_period(&lease.endpoint_id,std::path::Path::new(dll),minimum,None)?;
        let handles=lease.grant_pcm(&guid_text(period.timing.source))?;
        let source=Self::map_period(period,handles,Some(lease))?;
        crate::session_control::source_ready(peer,&source.lease.as_ref().unwrap().endpoint_id)?;
        Ok(source)
    }
    pub(crate) fn open_prepared(endpoint:&str,minimum:bool,cancelled:HANDLE,until:u64)->io::Result<OpenedPeriod> {
        let image=std::env::current_exe()?;
        let dll=image.parent().ok_or_else(||invalid("worker directory missing"))?.join("deskfuse-source-control.dll");
        Self::open_period(endpoint,&dll,minimum,Some((cancelled,until)))
    }
    fn open_period(endpoint:&str,dll:&std::path::Path,minimum:bool,authority:Option<(HANDLE,u64)>)->io::Result<OpenedPeriod> {
        use std::os::windows::ffi::OsStrExt;
        if !dll.is_absolute() {return Err(invalid("source DLL requires absolute path"))}
        let path:Vec<u16>=dll.as_os_str().encode_wide().chain(Some(0)).collect();
        unsafe {
            let module=LoadLibraryExW(path.as_ptr(),std::ptr::null_mut(),LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
            if module.is_null(){return Err(io::Error::last_os_error())}
            let symbol = if authority.is_some() { b"deskfuse_source_open_prepared\0".as_slice() }
                else { b"deskfuse_source_open\0".as_slice() };
            let open=GetProcAddress(module,symbol.as_ptr());
            let service=GetProcAddress(module,b"deskfuse_source_service\0".as_ptr());
            let close=GetProcAddress(module,b"deskfuse_source_close\0".as_ptr());
            let period=GetProcAddress(module,b"deskfuse_source_period_frames\0".as_ptr());
            if open.is_none() || service.is_none() || close.is_none() || period.is_none(){FreeLibrary(module);return Err(invalid("source DLL ABI unavailable"))}
            let endpoint:Vec<u16>=endpoint.encode_utf16().chain(Some(0)).collect();
            let mut timing=Timing::default();let mut event=std::ptr::null_mut();
            let object=if let Some((cancelled,until))=authority {
                let open:unsafe extern "C" fn(*const u16,u32,HANDLE,u64,*mut Timing,*mut HANDLE)->*mut c_void=std::mem::transmute(open);
                open(endpoint.as_ptr(),minimum as u32,cancelled,until,&mut timing,&mut event)
            } else {
                let open:unsafe extern "C" fn(*const u16,u32,*mut Timing,*mut HANDLE)->*mut c_void=std::mem::transmute(open);
                open(endpoint.as_ptr(),minimum as u32,&mut timing,&mut event)
            };
            if object.is_null(){FreeLibrary(module);return Err(invalid("running endpoint/period/export contract refused"))}
            let native=NativeControl{module,object,event,service:std::mem::transmute(service),close:std::mem::transmute(close)};
            let period:unsafe extern "C" fn(*mut c_void)->u32=std::mem::transmute(period);
            let frames=period(object);
            if event.is_null() || !(1..=240).contains(&frames){return Err(invalid("actual direct engine period not qualified"))}
            if timing.size as usize!=std::mem::size_of::<Timing>() || timing.abi!=1 || timing.build!=0x2026102502 ||
                timing.flags&3!=3 || timing.export_abi!=ABI || timing.source==[0;16] {return Err(invalid("driver timing/source ABI mismatch"))}
            Ok(OpenedPeriod{native,timing,frames})
        }
    }
    pub(crate) fn map_prepared(period:OpenedPeriod,handles:[u64;4])->io::Result<Self> {
        Self::map_period(period,handles,None)
    }
    fn map_period(period:OpenedPeriod,handles:[u64;4],lease:Option<EndpointLease>)->io::Result<Self> {
        let [file,section,event]=handles[..3] else { unreachable!() };
        if [file,section,event].iter().any(|&h| h==0 || h>isize::MAX as u64)
            || file==section || file==event || section==event || handles[3]==0 {
            return Err(invalid("invalid PCM handle offer"));
        }
        unsafe {
            let file=std::fs::File::from_raw_handle(handles[0] as HANDLE);
            let section=handles[1] as HANDLE;let source_event=handles[2] as HANDLE;
            let view=MapViewOfFile(section,FILE_MAP_READ,0,0,std::mem::size_of::<Mapping>());
            if view.Value.is_null(){CloseHandle(section);CloseHandle(source_event);return Err(io::Error::last_os_error())}
            let broker_owned=lease.is_none();
            let source=Self{file,section,event:source_event,map:view.Value.cast(),native:period.native,lease,broker_owned,generation:handles[3],
                ticket:0,offset:0,slot:None,geometry_reported:false,have:0,next_frame:None,first:0,oldest:0,min_commit:0,pcm:[0;960]};
            let control=source.snapshot()?;
            let header = std::ptr::addr_of!((*source.map).control);
            if control.source != period.timing.source ||
                std::ptr::read_volatile(std::ptr::addr_of!((*header).source)) != control.source ||
                std::ptr::read_volatile(std::ptr::addr_of!((*header).generation)) != control.generation ||
                std::ptr::read_volatile(std::ptr::addr_of!((*header).abi)) != control.abi ||
                std::ptr::read_volatile(std::ptr::addr_of!((*header).size)) != control.size {
                return Err(invalid("mapped source differs from exact endpoint or file"));
            }
            Ok(source)
        }
    }
    pub fn period_event(&self)->HANDLE {self.native.event}
    pub fn service_period(&self)->io::Result<()> {
        if unsafe{(self.native.service)(self.native.object)}!=0{return Err(invalid("period owner failed; source retired"))}Ok(())
    }
    fn ioctl<T>(&self,code:u32,input:Option<&Ack>,output:&mut T)->io::Result<()> {
        let mut returned=0;
        let (ptr,len)=input.map_or((std::ptr::null(),0),|a|(a as *const _ as *const c_void,std::mem::size_of::<Ack>() as u32));
        if unsafe{DeviceIoControl(self.file.as_raw_handle(),code,ptr,len,output as *mut _ as *mut c_void,
            std::mem::size_of::<T>() as u32,&mut returned,std::ptr::null_mut())}==0 {return Err(io::Error::last_os_error())}
        Ok(())
    }
    pub fn snapshot(&self)->io::Result<Control> {
        let mut c=Control::default();
        #[cfg(test)]
        if self.native.module.is_null() { c = unsafe { std::ptr::read_volatile(&(*self.map).control) }; }
        else { self.ioctl(SNAPSHOT,None,&mut c)?; }
        #[cfg(not(test))]
        self.ioctl(SNAPSHOT,None,&mut c)?;
        if let Err(error) = c.geometry.validate() {
            crate::startup_trace::write(&format!("PCM state={} generation={} expected_generation={} geometry={:?}",c.state,c.generation,self.generation,c.geometry));
            return Err(error);
        }
        if c.size as usize!=std::mem::size_of::<Control>() || c.abi!=ABI || c.generation!=self.generation || c.state>1 ||
            c.acknowledged>c.published || c.published-c.acknowledged>8 {
            crate::startup_trace::write(&format!("PCM state={} generation={} expected_generation={} size={} abi={} geometry={:?}",c.state,c.generation,self.generation,c.size,c.abi,c.geometry));
            return Err(invalid("source control retired/incompatible"))}Ok(c)
    }
    pub fn fence(&mut self,request_qpc:u64)->io::Result<CommitFence> {
        let mut c=Control::default();self.ioctl(FENCE,None,&mut c)?;
        if c.generation!=self.generation || c.state>1{return Err(invalid("source changed during recovery fence"))}
        self.have=0;self.next_frame=None;
        Ok(CommitFence{generation:c.generation,accepted:c.accepted_commit,end_frame:c.accepted_end_frame,request_qpc})
    }
    fn acknowledge(&self,c:&Control)->io::Result<()> {
        #[cfg(test)]
        if self.native.module.is_null() { return Ok(()); }
        self.ioctl(ACK,Some(&Ack{generation:self.generation,ticket:self.ticket,control:c.control_sequence}),&mut 0u64)
    }
    pub fn arm(&self)->io::Result<()> {let c=self.snapshot()?;self.acknowledge(&c)}
    pub fn next_block(&mut self)->io::Result<Option<Block>> {
        let c=self.snapshot()?;
        if c.state!=1 || !c.geometry.validate()? {self.acknowledge(&c)?;return Ok(None)}
        if !self.geometry_reported {
            eprintln!("DIRECT_GEOMETRY geometry_qualified=1 packet_frames={} notifications={} sample_rate={} channels={} bits={}",c.geometry.packet,c.geometry.notifications,c.geometry.rate,c.geometry.channels,c.geometry.bits);
            self.geometry_reported=true;
        }
        if !c.correlation_qpc.gt(&0){return Err(invalid("RUN without source-clock correlation"))}
        for _ in 0..8 {
            if self.slot.is_none() {
                if self.ticket>=c.published {self.acknowledge(&c)?;return Ok(None)}
                let h=unsafe{std::ptr::read_volatile(&(*self.map).slots[(self.ticket%8)as usize].header)};
                std::sync::atomic::fence(std::sync::atomic::Ordering::Acquire);
                if h.ticket!=self.ticket+1 || h.generation!=self.generation || h.frames==0 || h.frames>c.geometry.packet || h.reserved!=0 ||
                    h.qpc==0 || h.frame.checked_add(h.frames as u64).is_none() {return Err(invalid("invalid/in-flight committed descriptor"))}
                self.slot=Some(h);self.offset=0;
            }
            let h=self.slot.unwrap();let frame=h.frame+self.offset as u64;
            if self.next_frame.is_some_and(|n|n!=frame){self.have=0;self.next_frame=None;}
            if self.have==0 {self.first=frame;self.oldest=h.qpc;self.min_commit=h.commit;}
            self.oldest=self.oldest.min(h.qpc);self.min_commit=self.min_commit.min(h.commit);
            let take=(240-self.have).min(h.frames as usize-self.offset);
            let pcm=unsafe{std::slice::from_raw_parts((*self.map).slots[(self.ticket%8)as usize].pcm.as_ptr().add(self.offset*4),take*4)};
            self.pcm[self.have*4..(self.have+take)*4].copy_from_slice(pcm);
            self.offset+=take;self.have+=take;self.next_frame=Some(frame+take as u64);
            if self.offset==h.frames as usize {self.ticket+=1;self.slot=None;self.acknowledge(&c)?;}
            if self.have==240 {
                let delta=i128::from(self.first)-i128::from(c.correlation_frame);
                let presentation=i128::from(c.correlation_qpc)+delta*10_000_000/48_000;
                let presentation=u64::try_from(presentation).map_err(|_|invalid("invalid presentation correlation"))?;
                let result=Block{first_frame:self.first,timing:CommitTiming{commit_qpc:self.oldest,presentation_qpc:presentation},
                    min_commit:self.min_commit,generation:self.generation,pcm:self.pcm};self.have=0;return Ok(Some(result))
            }
        }
        Ok(None)
    }
    pub fn release_lease(&mut self){drop(self.lease.take());}
    pub fn stop(&mut self) {
        let mut returned=0;
        if !self.broker_owned {
            unsafe {DeviceIoControl(self.file.as_raw_handle(),REVOKE,std::ptr::null(),0,std::ptr::null_mut(),0,&mut returned,std::ptr::null_mut());}
        }
        if !self.native.object.is_null(){unsafe{(self.native.close)(self.native.object);}self.native.object=std::ptr::null_mut();}
    }
}
impl Drop for Source {fn drop(&mut self){self.stop();unsafe{UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS{Value:self.map as *mut c_void});CloseHandle(self.section);CloseHandle(self.event);}}}
fn guid_text(g:[u8;16])->String {
    format!("{:08x}-{:04x}-{:04x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        u32::from_le_bytes(g[0..4].try_into().unwrap()),u16::from_le_bytes(g[4..6].try_into().unwrap()),
        u16::from_le_bytes(g[6..8].try_into().unwrap()),g[8],g[9],g[10],g[11],g[12],g[13],g[14],g[15])
}

#[cfg(test)]
pub(crate) fn fixture_pcm(source: [u8; 16], generation: u64) -> [u64; 4] {
    use windows_sys::Win32::System::Threading::CreateEventW;
    unsafe {
        let file = CreateEventW(std::ptr::null(), 0, 0, std::ptr::null());
        let ready = CreateEventW(std::ptr::null(), 0, 0, std::ptr::null());
        let section = CreateFileMappingW(INVALID_HANDLE_VALUE, std::ptr::null(), PAGE_READWRITE,
            0, std::mem::size_of::<Mapping>() as u32, std::ptr::null());
        assert!(!file.is_null() && !ready.is_null() && !section.is_null());
        let view = MapViewOfFile(section, FILE_MAP_WRITE, 0, 0, std::mem::size_of::<Mapping>());
        assert!(!view.Value.is_null());
        (*view.Value.cast::<Mapping>()).control = Control {
            size: std::mem::size_of::<Control>() as u32, abi: ABI, source, generation, ..Control::default()
        };
        UnmapViewOfFile(view);
        [file as u64, section as u64, ready as u64, generation]
    }
}

/// Endpoint/COM/mapping setup is off the QUIC owner. This one-item channel
/// transfers ownership once; it never carries PCM.
pub struct Setup {rx:std::sync::mpsc::Receiver<io::Result<Source>>,worker:Option<std::thread::JoinHandle<()>>,cancel:Arc<AtomicBool>}
impl Setup {
    pub fn start(peer:String,dll:String,minimum:bool,mut wake:std::net::SocketAddr)->Self {
        if wake.ip().is_unspecified(){wake.set_ip("127.0.0.1".parse().unwrap());}
        let (tx,rx)=std::sync::mpsc::sync_channel(1);let cancel=Arc::new(AtomicBool::new(false));let flag=Arc::clone(&cancel);
        let worker=std::thread::spawn(move||{let _=tx.send(Source::open(&peer,&dll,minimum,&flag));
            if let Ok(socket)=std::net::UdpSocket::bind("127.0.0.1:0"){let _=socket.send_to(&[0],wake);}});
        Self{rx,worker:Some(worker),cancel}
    }
    pub fn try_take(&self)->Result<io::Result<Source>,std::sync::mpsc::TryRecvError>{self.rx.try_recv()}
    pub fn finish(mut self)->Option<io::Result<Source>> {
        self.cancel.store(true,std::sync::atomic::Ordering::Release);
        if let Some(worker)=self.worker.take(){let _=worker.join();}
        self.rx.try_recv().ok()
    }
}
impl Drop for Setup {fn drop(&mut self){self.cancel.store(true,std::sync::atomic::Ordering::Release);if let Some(worker)=self.worker.take(){let _=worker.join();}}}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn readonly_section_view_survives_creator_release_and_new_storage() {
        unsafe {
            let section=CreateFileMappingW(INVALID_HANDLE_VALUE,std::ptr::null(),PAGE_READWRITE,0,4096,std::ptr::null());
            assert!(!section.is_null());
            let writer=MapViewOfFile(section,FILE_MAP_WRITE,0,0,4096);assert!(!writer.Value.is_null());
            std::ptr::write_bytes(writer.Value,0x5a,4096);
            let reader=MapViewOfFile(section,FILE_MAP_READ,0,0,4096);assert!(!reader.Value.is_null());
            UnmapViewOfFile(writer);CloseHandle(section);
            let replacement=CreateFileMappingW(INVALID_HANDLE_VALUE,std::ptr::null(),PAGE_READWRITE,0,4096,std::ptr::null());
            assert!(!replacement.is_null());
            let new_writer=MapViewOfFile(replacement,FILE_MAP_WRITE,0,0,4096);assert!(!new_writer.Value.is_null());
            std::ptr::write_bytes(new_writer.Value,0xa5,4096);
            assert!(std::slice::from_raw_parts(reader.Value as *const u8,4096).iter().all(|b|*b==0x5a));
            let mut info:MEMORY_BASIC_INFORMATION=std::mem::zeroed();
            assert_ne!(VirtualQuery(reader.Value,&mut info,std::mem::size_of_val(&info)),0);
            assert_eq!(info.Protect,PAGE_READONLY);
            UnmapViewOfFile(reader);UnmapViewOfFile(new_writer);CloseHandle(replacement);
        }
    }
    #[test]
    fn actual_geometry_qualifies_the_entire_future_packet() {
        assert!(!Geometry::default().validate().unwrap());
        let good=Geometry{state:1,packet:128,notifications:2,rate:48000,channels:2,bits:16};
        assert!(good.validate().unwrap());
        assert!(Geometry{packet:240,..good}.validate().unwrap());
        assert!(Geometry{packet:480,..good}.validate().is_err());
        assert!(Geometry{notifications:1,..good}.validate().is_err());
        assert!(Geometry{state:2,..good}.validate().is_err());
        assert!(crate::source_age::CommitTiming{commit_qpc:1_000_000,presentation_qpc:1_100_000}.classify(1_000_000)==crate::source_age::AgeDecision::Fresh{age_100ns:0});
    }
    #[test]
    fn driver_mapping_layout_is_exact(){
        assert_eq!(std::mem::size_of::<Control>(),128);
        assert_eq!(std::mem::size_of::<Slot>(),3888);
        assert_eq!(std::mem::size_of::<Mapping>(),31232);
    }
}
