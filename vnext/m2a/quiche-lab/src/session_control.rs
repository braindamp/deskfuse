//! One approved product session. Control only; never handles PCM.
use std::{io,net::{SocketAddr,UdpSocket},sync::{Mutex,atomic::{AtomicBool,Ordering}}};
static ACTIVE:AtomicBool=AtomicBool::new(false);
static STOP:AtomicBool=AtomicBool::new(false);
static PIN:Mutex<Option<Vec<u8>>>=Mutex::new(None);
static WAKE:Mutex<Option<SocketAddr>>=Mutex::new(None);
pub struct Session;
impl Session {
    pub fn begin(pin:Vec<u8>)->io::Result<Self> {
        if pin.is_empty() || pin.len()>16384 || pin[0]!=0x30 ||
            ACTIVE.compare_exchange(false,true,Ordering::AcqRel,Ordering::Acquire).is_err() {
            return Err(io::Error::new(io::ErrorKind::PermissionDenied,"invalid/busy native authorization"));
        }
        #[cfg(not(target_os="android"))]
        STOP.store(false,Ordering::Release);
        #[cfg(target_os="android")]
        if STOP.load(Ordering::Acquire) {ACTIVE.store(false,Ordering::Release);return Err(io::Error::from(io::ErrorKind::Interrupted))}
        *PIN.lock().unwrap()=Some(pin); Ok(Self)
    }
}
impl Drop for Session {
    fn drop(&mut self) {
        *WAKE.lock().unwrap()=None;*PIN.lock().unwrap()=None;
        ACTIVE.store(false,Ordering::Release);
    }
}
pub fn active()->bool {ACTIVE.load(Ordering::Acquire)}
pub fn prepare()->bool {
    if active() {return false}
    STOP.store(false,Ordering::Release); true
}
pub fn stopping()->bool {active() && STOP.load(Ordering::Acquire)}
pub fn verify(peer:Option<&[u8]>)->io::Result<()> {
    if !active() {return Ok(())}
    let pin=PIN.lock().unwrap();
    if pin.as_deref()!=peer {return Err(io::Error::new(io::ErrorKind::PermissionDenied,"session certificate binding mismatch"))}
    Ok(())
}
pub fn register_socket(socket:&UdpSocket)->io::Result<()> {
    if active() {
        let mut addr=socket.local_addr()?;
        if addr.ip().is_unspecified() {addr.set_ip("127.0.0.1".parse().unwrap());}
        *WAKE.lock().unwrap()=Some(addr);
    }
    Ok(())
}
pub fn stop() {
    STOP.store(true,Ordering::Release);
    if let Some(addr)=*WAKE.lock().unwrap() {
        if let Ok(wake)=UdpSocket::bind("127.0.0.1:0") {let _=wake.send_to(&[0],addr);}
    }
}
pub fn bounded_stdin()->io::Result<serde_json::Value> {
    use io::Read;
    let mut bytes=Vec::with_capacity(1024);
    let mut stdin=io::stdin().lock();
    loop {
        let mut b=[0u8;1];stdin.read_exact(&mut b)?;
        if b[0]==b'\n' {break}
        if bytes.len()==16384 {return Err(io::Error::from(io::ErrorKind::InvalidData))}
        bytes.push(b[0]);
    }
    Ok(serde_json::from_slice(&bytes)?)
}
