//! The only source buffer is the immutable kernel slot plus the required
//! 240-frame wire assembler. No PCM pipe, reader queue, pacing thread or ring.
use std::{io,ffi::c_void,sync::{Arc,atomic::AtomicBool},os::windows::io::{FromRawHandle,AsRawHandle}};
use crate::{endpoint_lease::EndpointLease,source_age::CommitTiming,fresh_anchor::CommitFence};
use windows_sys::Win32::{Foundation::*,System::{Memory::*,LibraryLoader::*,IO::DeviceIoControl}};
const ABI:u32=1;const SNAPSHOT:u32=0x22e004;const ACK:u32=0x22e008;const FENCE:u32=0x22e00c;const REVOKE:u32=0x22e010;
#[repr(C)] #[derive(Clone,Copy,Default,Debug)]
pub struct Control {
    pub size:u32,pub abi:u32,pub state:u32,pub reserved:u32,pub source:[u8;16],
    pub generation:u64,pub control_sequence:u64,pub accepted_commit:u64,pub accepted_end_frame:u64,pub dropped_frames:u64,
    pub correlation_frame:u64,pub correlation_qpc:u64,pub published:u64,pub acknowledged:u64,
}
#[repr(C)] #[derive(Clone,Copy,Default)]
struct SlotHeader {ticket:u64,generation:u64,commit:u64,qpc:u64,frame:u64,frames:u32,reserved:u32}
#[repr(C)] struct Slot {header:SlotHeader,pcm:[u8;3840]}
#[repr(C)] struct Mapping {control:Control,slots:[Slot;8]}
#[repr(C)] #[derive(Default)] struct Timing {size:u32,abi:u32,build:u64,source:[u8;16],flags:u32,export_abi:u32}
#[repr(C)] struct Ack {generation:u64,ticket:u64,control:u64}
pub struct Block {pub first_frame:u64,pub timing:CommitTiming,pub min_commit:u64,pub generation:u64,pub pcm:[u8;960]}
struct NativeControl {
    module:HMODULE,object:*mut c_void,pub event:HANDLE,
    service:unsafe extern "C" fn(*mut c_void)->i32,close:unsafe extern "C" fn(*mut c_void),
}
impl Drop for NativeControl {fn drop(&mut self){unsafe{if !self.object.is_null(){(self.close)(self.object);}FreeLibrary(self.module);}}}
unsafe impl Send for NativeControl {} // opaque event/thread owner; no COM services cross threads
pub struct Source {
    file:std::fs::File,section:HANDLE,pub event:HANDLE,map:*const Mapping,native:NativeControl,
    lease:Option<EndpointLease>,generation:u64,ticket:u64,offset:usize,slot:Option<SlotHeader>,
    have:usize,next_frame:Option<u64>,first:u64,oldest:u64,min_commit:u64,pcm:[u8;960],
}
unsafe impl Send for Source {} // transferred once from setup; used by ONE owner thereafter
fn invalid(s:&str)->io::Error {io::Error::new(io::ErrorKind::InvalidData,s)}
impl Source {
    pub fn open(peer:&str,dll:&str,minimum:bool,cancel:&AtomicBool)->io::Result<Self> {
        let lease=EndpointLease::provision(peer,cancel)?;
        let path:Vec<u16>=dll.encode_utf16().chain(Some(0)).collect();
        if !std::path::Path::new(dll).is_absolute() {return Err(invalid("source DLL requires absolute path"))}
        unsafe {
            let module=LoadLibraryExW(path.as_ptr(),std::ptr::null_mut(),LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
            if module.is_null(){return Err(io::Error::last_os_error())}
            let open=GetProcAddress(module,b"deskfuse_source_open\0".as_ptr());
            let service=GetProcAddress(module,b"deskfuse_source_service\0".as_ptr());
            let close=GetProcAddress(module,b"deskfuse_source_close\0".as_ptr());
            if open.is_none() || service.is_none() || close.is_none(){FreeLibrary(module);return Err(invalid("source DLL ABI unavailable"))}
            let open:unsafe extern "C" fn(*const u16,u32,*mut Timing,*mut HANDLE)->*mut c_void=std::mem::transmute(open);
            let endpoint:Vec<u16>=lease.endpoint_id.encode_utf16().chain(Some(0)).collect();
            let mut timing=Timing::default();let mut event=std::ptr::null_mut();
            let object=open(endpoint.as_ptr(),minimum as u32,&mut timing,&mut event);
            if object.is_null(){FreeLibrary(module);return Err(invalid("running endpoint/period/export contract refused"))}
            let native=NativeControl{module,object,event,service:std::mem::transmute(service),close:std::mem::transmute(close)};
            if timing.size as usize!=std::mem::size_of::<Timing>() || timing.abi!=1 || timing.build!=0x2026102501 ||
                timing.flags&3!=3 || timing.export_abi!=ABI || timing.source==[0;16] {return Err(invalid("driver timing/source ABI mismatch"))}
            let handles=lease.grant_pcm(&guid_text(timing.source))?;
            let file=std::fs::File::from_raw_handle(handles[0] as HANDLE);
            let section=handles[1] as HANDLE;let source_event=handles[2] as HANDLE;
            let view=MapViewOfFile(section,FILE_MAP_READ,0,0,std::mem::size_of::<Mapping>());
            if view.Value.is_null(){CloseHandle(section);CloseHandle(source_event);return Err(io::Error::last_os_error())}
            let source=Self{file,section,event:source_event,map:view.Value.cast(),native,lease:Some(lease),generation:handles[3],
                ticket:0,offset:0,slot:None,have:0,next_frame:None,first:0,oldest:0,min_commit:0,pcm:[0;960]};
            let control=source.snapshot()?;
            if control.source!=timing.source{return Err(invalid("mapped source differs from exact endpoint"))}
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
        let mut c=Control::default();self.ioctl(SNAPSHOT,None,&mut c)?;
        if c.size as usize!=std::mem::size_of::<Control>() || c.abi!=ABI || c.generation!=self.generation || c.state>1 ||
            c.acknowledged>c.published || c.published-c.acknowledged>8 {return Err(invalid("source control retired/incompatible"))}Ok(c)
    }
    pub fn fence(&mut self,request_qpc:u64)->io::Result<CommitFence> {
        let mut c=Control::default();self.ioctl(FENCE,None,&mut c)?;
        if c.generation!=self.generation || c.state>1{return Err(invalid("source changed during recovery fence"))}
        self.have=0;self.next_frame=None;
        Ok(CommitFence{generation:c.generation,accepted:c.accepted_commit,end_frame:c.accepted_end_frame,request_qpc})
    }
    fn acknowledge(&self,c:&Control)->io::Result<()> {
        self.ioctl(ACK,Some(&Ack{generation:self.generation,ticket:self.ticket,control:c.control_sequence}),&mut 0u64)
    }
    pub fn arm(&self)->io::Result<()> {let c=self.snapshot()?;self.acknowledge(&c)}
    pub fn next_block(&mut self)->io::Result<Option<Block>> {
        let c=self.snapshot()?;
        if c.state!=1 {self.acknowledge(&c)?;return Ok(None)}
        if !c.correlation_qpc.gt(&0){return Err(invalid("RUN without source-clock correlation"))}
        for _ in 0..8 {
            if self.slot.is_none() {
                if self.ticket>=c.published {self.acknowledge(&c)?;return Ok(None)}
                let h=unsafe{std::ptr::read_volatile(&(*self.map).slots[(self.ticket%8)as usize].header)};
                std::sync::atomic::fence(std::sync::atomic::Ordering::Acquire);
                if h.ticket!=self.ticket+1 || h.generation!=self.generation || h.frames==0 || h.frames>960 || h.reserved!=0 ||
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
        unsafe {DeviceIoControl(self.file.as_raw_handle(),REVOKE,std::ptr::null(),0,std::ptr::null_mut(),0,&mut returned,std::ptr::null_mut());}
        if !self.native.object.is_null(){unsafe{(self.native.close)(self.native.object);}self.native.object=std::ptr::null_mut();}
    }
}
impl Drop for Source {fn drop(&mut self){self.stop();unsafe{UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS{Value:self.map as *mut c_void});CloseHandle(self.section);CloseHandle(self.event);}}}
fn guid_text(g:[u8;16])->String {
    format!("{:08x}-{:04x}-{:04x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        u32::from_le_bytes(g[0..4].try_into().unwrap()),u16::from_le_bytes(g[4..6].try_into().unwrap()),
        u16::from_le_bytes(g[6..8].try_into().unwrap()),g[8],g[9],g[10],g[11],g[12],g[13],g[14],g[15])
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
    fn driver_mapping_layout_is_exact(){
        assert_eq!(std::mem::size_of::<Control>(),104);
        assert_eq!(std::mem::size_of::<Slot>(),3888);
        assert_eq!(std::mem::size_of::<Mapping>(),31208);
    }
}
