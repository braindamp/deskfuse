//! Control-only lease of an enrolled phone's explicit DeskFuse render endpoint.
//! Call only AFTER certificate authentication/pinning. No PCM, routing,
//! driver installation, default restoration, or realtime timer lives here.
use std::io;

pub fn validate_peer_id(peer: &str) -> io::Result<String> {
    if peer.len() != 32 || !peer.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "enrolled peer ID must be 32 hexadecimal characters"));
    }
    Ok(peer.to_ascii_lowercase())
}

pub struct EndpointLease {
    pub endpoint_id: String,
    #[cfg(windows)]
    peer_id: String,
    #[cfg(windows)]
    nonce:String,
    #[cfg(windows)]
    _pipe: std::fs::File,
}

impl EndpointLease {
    #[cfg(windows)]
    pub fn grant_pcm(&self,source:&str)->io::Result<[u64;4]> {
        use std::io::{Read,Write};
        let mut pipe=&self._pipe;
        writeln!(pipe,"{}",serde_json::json!({"Op":"grant_pcm","PeerId":self.peer_id,"SourceInstance":source,"ClientLeaseNonce":self.nonce}))?;
        let mut reply=Vec::with_capacity(512);
        let deadline=std::time::Instant::now()+std::time::Duration::from_secs(5);
        loop {
            let mut byte=[0u8;1];
            match pipe.read(&mut byte) {
                Ok(1) if byte[0]==b'\n'=>break,
                Ok(1)=>reply.push(byte[0]),
                Ok(_)=>return Err(io::Error::from(io::ErrorKind::UnexpectedEof)),
                Err(e) if e.raw_os_error()==Some(232) || e.kind()==io::ErrorKind::WouldBlock=> {
                    if std::time::Instant::now()>=deadline{return Err(io::Error::from(io::ErrorKind::TimedOut))}
                    std::thread::sleep(std::time::Duration::from_millis(5));
                }
                Err(e)=>return Err(e),
            }
            if reply.len()>16384{return Err(io::Error::from(io::ErrorKind::InvalidData))}
        }
        let value:serde_json::Value=serde_json::from_slice(&reply)?;
        if value["Ok"].as_bool()!=Some(true){return Err(io::Error::new(io::ErrorKind::PermissionDenied,"broker denied exact-endpoint PCM grant"))}
        let mut handles=[0;4];
        for (i,name) in ["PcmFile","PcmSection","PcmEvent","PcmGeneration"].iter().enumerate() {
            handles[i]=value[name].as_u64().filter(|n|*n!=0 && *n!=u64::MAX)
                .ok_or_else(||io::Error::new(io::ErrorKind::InvalidData,"broker PCM grant invalid"))?;
        }
        Ok(handles)
    }
    #[cfg(not(windows))]
    pub fn provision(_: &str, _: &std::sync::atomic::AtomicBool) -> io::Result<Self> {
        Err(io::Error::new(io::ErrorKind::Unsupported, "DeskFuse endpoints require Windows"))
    }

    #[cfg(windows)]
    pub fn provision(peer: &str, cancel: &std::sync::atomic::AtomicBool) -> io::Result<Self> {
        use std::io::{Read, Write};
        use std::os::windows::fs::OpenOptionsExt;
        use std::os::windows::io::AsRawHandle;
        use std::time::{Duration, Instant};
        #[link(name = "Kernel32")]
        unsafe extern "system" {
            fn WaitNamedPipeW(name: *const u16, milliseconds: u32) -> i32;
            fn SetNamedPipeHandleState(handle: *mut std::ffi::c_void, mode: *const u32,
                count: *const u32, timeout: *const u32) -> i32;
        }
        let peer = validate_peer_id(peer)?;
        let name = r"\\.\pipe\DeskFuse.AudioBroker.v1";
        let wide: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
        let connect_deadline = Instant::now() + Duration::from_secs(3);
        let mut pipe = loop {
            if cancel.load(std::sync::atomic::Ordering::Acquire) {
                return Err(io::Error::new(io::ErrorKind::Interrupted, "endpoint provisioning cancelled"));
            }
            // Identification-only SQOS: never allow the server to impersonate us.
            match std::fs::OpenOptions::new().read(true).write(true)
                .custom_flags(0x0010_0000 | 0x0001_0000).open(name) {
                Ok(pipe) => break pipe,
                Err(e) if e.raw_os_error() == Some(231) => {
                    let remaining = connect_deadline.saturating_duration_since(Instant::now());
                    if remaining.is_zero() { return Err(io::Error::new(io::ErrorKind::TimedOut, "endpoint broker busy")); }
                    if unsafe { WaitNamedPipeW(wide.as_ptr(), remaining.as_millis().min(3000) as u32) } == 0 {
                        return Err(io::Error::last_os_error());
                    }
                }
                Err(e) => return Err(e),
            }
        };
        // Nonblocking byte mode bounds setup reads even if a broker stops replying.
        // This setup-only wait is complete before any PCM/QUIC media starts.
        let mode = 1u32; // PIPE_READMODE_BYTE | PIPE_NOWAIT
        if unsafe { SetNamedPipeHandleState(pipe.as_raw_handle(), &mode,
            std::ptr::null(), std::ptr::null()) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut request = serde_json::to_vec(&serde_json::json!({
            "Op":"create", "PeerId":peer, "Label":"Audio Lab"
        }))?;
        request.push(b'\n');
        pipe.write_all(&request)?;
        let deadline = Instant::now() + Duration::from_secs(30);
        let mut reply = Vec::with_capacity(1024);
        loop {
            if cancel.load(std::sync::atomic::Ordering::Acquire) {
                return Err(io::Error::new(io::ErrorKind::Interrupted, "endpoint provisioning cancelled"));
            }
            if Instant::now() >= deadline {
                return Err(io::Error::new(io::ErrorKind::TimedOut, "endpoint broker provision timeout"));
            }
            let mut byte = [0u8; 1];
            match pipe.read(&mut byte) {
                Ok(0) => return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "endpoint broker closed")),
                Ok(_) if byte[0] == b'\n' => break,
                Ok(_) => {
                    if reply.len() >= 16 * 1024 {
                        return Err(io::Error::new(io::ErrorKind::InvalidData, "endpoint broker reply too large"));
                    }
                    reply.push(byte[0]);
                }
                Err(e) if e.raw_os_error() == Some(232) || e.kind() == io::ErrorKind::WouldBlock => {
                    std::thread::sleep(Duration::from_millis(10));
                }
                Err(e) => return Err(e),
            }
        }
        let endpoint_id = validate_reply(&peer, &reply)?;
        let value:serde_json::Value=serde_json::from_slice(&reply)?;
        let nonce=value["ClientLeaseNonce"].as_str().filter(|s|s.len()==64 && s.bytes().all(|b|b.is_ascii_hexdigit()))
            .ok_or_else(||io::Error::new(io::ErrorKind::PermissionDenied,"broker lacks session process-binding challenge"))?.to_owned();
        Ok(Self { endpoint_id, peer_id:peer, nonce, _pipe:pipe })
    }

    /// Child is still CREATE_SUSPENDED. Broker validates the actual pipe
    /// process and the opened child's parent before assigning its kill job.
    #[cfg(windows)]
    pub fn register_capture(&self, pid:u32) -> io::Result<()> {
        use std::io::{Read,Write};
        let mut pipe=&self._pipe;
        writeln!(pipe,"{}",serde_json::json!({"Op":"register_capture",
            "PeerId":self.peer_id,"ChildProcessId":pid,"ClientLeaseNonce":self.nonce}))?;
        let deadline=std::time::Instant::now()+std::time::Duration::from_secs(5);
        let mut bytes=Vec::with_capacity(256);
        loop {
            let mut b=[0u8;1];
            match pipe.read(&mut b) {
                Ok(1) if b[0]==b'\n'=>break,
                Ok(1)=>bytes.push(b[0]),
                Ok(_)=>return Err(io::Error::from(io::ErrorKind::UnexpectedEof)),
                Err(e) if e.raw_os_error()==Some(232) || e.kind()==io::ErrorKind::WouldBlock=> {
                    if std::time::Instant::now()>=deadline {return Err(io::Error::from(io::ErrorKind::TimedOut))}
                    std::thread::sleep(std::time::Duration::from_millis(5));
                }
                Err(e)=>return Err(e),
            }
            if bytes.len()>16*1024 {return Err(io::Error::from(io::ErrorKind::InvalidData))}
        }
        let reply:serde_json::Value=serde_json::from_slice(&bytes)?;
        if reply["Ok"].as_bool()!=Some(true) {
            return Err(io::Error::new(io::ErrorKind::PermissionDenied,"broker refused capture lifetime registration"));
        }
        Ok(())
    }
}

#[cfg(windows)]
fn validate_reply(peer: &str, bytes: &[u8]) -> io::Result<String> {
    let reply: serde_json::Value = serde_json::from_slice(bytes)?;
    let invalid = |s| io::Error::new(io::ErrorKind::InvalidData, s);
    if reply.get("Ok").and_then(|x| x.as_bool()) != Some(true) {
        return Err(invalid(format!("endpoint broker refused: {}",
            reply.get("Error").and_then(|x| x.as_str()).unwrap_or("no reason supplied"))));
    }
    let expected = format!(r"Root\DeskFuseAudio\deskfuse-speakers-{peer}");
    if !reply.get("PeerId").and_then(|x| x.as_str()).is_some_and(|x| x.eq_ignore_ascii_case(peer)) ||
        !reply.get("DeviceInstanceId").and_then(|x| x.as_str()).is_some_and(|x| x.eq_ignore_ascii_case(&expected)) {
        return Err(invalid("broker returned another peer/device instance".to_string()));
    }
    let endpoint = reply.get("EndpointId").and_then(|x| x.as_str())
        .ok_or_else(|| invalid("broker omitted endpoint ID".to_string()))?;
    let prefix = "{0.0.0.00000000}.";
    let guid = endpoint.strip_prefix(prefix).unwrap_or("");
    if guid.len() != 38 || !guid.is_ascii() || !guid.starts_with('{') || !guid.ends_with('}') ||
        !guid[1..37].bytes().enumerate().all(|(i,b)| {
            if [8,13,18,23].contains(&i) { b == b'-' } else { b.is_ascii_hexdigit() }
        }) {
        return Err(invalid("broker returned invalid render endpoint ID".to_string()));
    }
    Ok(endpoint.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn enrolled_identity_cannot_be_a_path_or_friendly_label() {
        assert_eq!(validate_peer_id("00112233445566778899AABBCCDDEEFF").unwrap(),
            "00112233445566778899aabbccddeeff");
        for invalid in ["default", "", r"Root\DeskFuseAudio\other", "00112233445566778899aabbccddeeff0"] {
            assert!(validate_peer_id(invalid).is_err());
        }
    }
    #[cfg(windows)]
    #[test]
    fn broker_reply_is_bound_to_full_peer_device_identity() {
        let peer = "00112233445566778899aabbccddeeff";
        let mut reply = serde_json::json!({"Ok":true,"PeerId":peer,
            "DeviceInstanceId":format!(r"Root\DeskFuseAudio\deskfuse-speakers-{peer}"),
            "EndpointId":"{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}"});
        assert!(validate_reply(peer, &serde_json::to_vec(&reply).unwrap()).is_ok());
        for field in ["PeerId", "DeviceInstanceId", "EndpointId"] {
            let original = reply[field].clone();
            reply[field] = serde_json::json!("default");
            assert!(validate_reply(peer, &serde_json::to_vec(&reply).unwrap()).is_err());
            reply[field] = original;
        }
    }
}
