//! Bounded, optional user-mode ETW diagnostics; no protocol or media writes.
use std::io;
use windows_sys::core::GUID;
#[link(name="advapi32")]
extern "system" {
    fn EventRegister(id:*const GUID,callback:*const core::ffi::c_void,context:*const core::ffi::c_void,handle:*mut u64)->u32;
    fn EventWriteString(handle:u64,level:u8,keyword:u64,text:*const u16)->u32;
    fn EventUnregister(handle:u64)->u32;
}
pub(crate) fn write(text:&str) {
    let text:Vec<u16>=text.chars().filter(|c| !c.is_control()).take(1024).collect::<String>().encode_utf16().chain(Some(0)).collect();
    let id=GUID::from_u128(0xe47848a1_9465_4513_9851_7e2deab15620);
    let mut handle=0;
    unsafe { if EventRegister(&id,std::ptr::null(),std::ptr::null(),&mut handle)==0 {
        EventWriteString(handle,4,1,text.as_ptr()); EventUnregister(handle);
    }}
}
pub(crate) fn failure(stage:&str,error:&io::Error) {
    write(&format!("NATIVE stage={stage} kind={:?} native={:?} detail={error}",error.kind(),error.raw_os_error()));
}
