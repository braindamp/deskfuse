//! Retained Windows pipe/server custody. Every overlapped request is completed
//! or cancelled AND joined before its buffer/event/OVERLAPPED can be released.
use super::*;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::ptr::{null, null_mut};
use std::time::{Duration, Instant};
use windows_sys::Win32::{Foundation::*, Globalization::CompareStringOrdinal,
    Security::{Authorization::{GetSecurityInfo, SE_KERNEL_OBJECT}, *},
    Storage::FileSystem::*, System::{IO::*, Pipes::*, Threading::*, WindowsProgramming::QueryInterruptTimePrecise}};

fn handle(raw: HANDLE) -> io::Result<OwnedHandle> {
    if raw.is_null() || raw == INVALID_HANDLE_VALUE { Err(io::Error::last_os_error()) }
    else { Ok(unsafe { OwnedHandle::from_raw_handle(raw) }) }
}
fn wide(value: &std::ffi::OsStr) -> Vec<u16> { value.encode_wide().chain(Some(0)).collect() }

struct Control {
    pipe: OwnedHandle,
    server: OwnedHandle,
    server_pid: u32,
    server_created: u64,
}

impl Control {
    fn connect(registration: [u8; 16]) -> io::Result<Self> {
        let hex: String = registration.iter().map(|b| format!("{b:02x}")).collect();
        let name = wide(std::ffi::OsStr::new(&format!(r"\\.\pipe\DeskFuse.Broker.Control.{hex}")));
        let start = Instant::now();
        let pipe = loop {
            let raw = unsafe { CreateFileW(name.as_ptr(), CLIENT_MASK, 0, null(), OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | 0x0011_0000, null_mut()) }; // SQOS identification: server cannot impersonate
            if raw != INVALID_HANDLE_VALUE { break handle(raw)?; }
            let error = io::Error::last_os_error();
            if error.raw_os_error() != Some(ERROR_PIPE_BUSY as i32) || start.elapsed() >= Duration::from_secs(3) { return Err(error); }
            // Initial busy retry only; never reconnect a once-opened channel.
            std::thread::sleep(Duration::from_millis(25));
        };
        unsafe { require_descriptor(pipe.as_raw_handle())?; }
        let mut pid = 0; let mut session = u32::MAX;
        if unsafe { GetNamedPipeServerProcessId(pipe.as_raw_handle(), &mut pid) } == 0 || pid == 0 ||
            unsafe { GetNamedPipeServerSessionId(pipe.as_raw_handle(), &mut session) } == 0 || session != 0 {
            return Err(invalid());
        }
        let server = handle(unsafe { OpenProcess(0x0010_1000, 0, pid) })?;
        unsafe { require_system(server.as_raw_handle())?; }
        let mut path = vec![0u16; 32768]; let mut length = path.len() as u32;
        if unsafe { QueryFullProcessImageNameW(server.as_raw_handle(), 0, path.as_mut_ptr(), &mut length) } == 0 { return Err(io::Error::last_os_error()); }
        let expected = std::env::current_exe()?.parent().ok_or_else(invalid)?.join("DeskFuse.AudioBroker.exe");
        let expected: Vec<u16> = expected.as_os_str().encode_wide().collect();
        if unsafe { CompareStringOrdinal(path.as_ptr(), length as i32, expected.as_ptr(), expected.len() as i32, 1) } != 2 {
            return Err(invalid());
        }
        let mut created: FILETIME = unsafe { std::mem::zeroed() };
        let mut exited: FILETIME = unsafe { std::mem::zeroed() };
        let mut kernel: FILETIME = unsafe { std::mem::zeroed() };
        let mut user: FILETIME = unsafe { std::mem::zeroed() };
        if unsafe { GetProcessTimes(server.as_raw_handle(), &mut created, &mut exited, &mut kernel, &mut user) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let created = (u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime);
        let mut again = 0;
        if created == 0 || unsafe { WaitForSingleObject(server.as_raw_handle(), 0) } != WAIT_TIMEOUT ||
            unsafe { GetNamedPipeServerProcessId(pipe.as_raw_handle(), &mut again) } == 0 || again != pid { return Err(invalid()); }
        Ok(Self { pipe, server, server_pid: pid, server_created: created })
    }

    fn transfer(&self, buffer: &mut [u8], write: bool, timeout_ms: u32) -> io::Result<usize> {
        if buffer.is_empty() || buffer.len() > MAX_FRAME + 4 { return Err(invalid()); }
        let event = handle(unsafe { CreateEventW(null(), 1, 0, null()) })?;
        let mut operation: OVERLAPPED = unsafe { std::mem::zeroed() }; operation.hEvent = event.as_raw_handle();
        let mut count = 0;
        let ok = unsafe { if write {
            WriteFile(self.pipe.as_raw_handle(), buffer.as_ptr(), buffer.len() as u32, &mut count, &mut operation)
        } else {
            ReadFile(self.pipe.as_raw_handle(), buffer.as_mut_ptr(), buffer.len() as u32, &mut count, &mut operation)
        }};
        if ok == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() != Some(ERROR_IO_PENDING as i32) { return Err(error); }
            let handles = [event.as_raw_handle(), self.server.as_raw_handle()];
            let result = unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, timeout_ms) };
            if result != WAIT_OBJECT_0 {
                // Even cancellation rejection/late completion does not permit
                // releasing operation storage until GetOverlappedResult joins.
                unsafe { CancelIoEx(self.pipe.as_raw_handle(), &operation); }
                unsafe { GetOverlappedResult(self.pipe.as_raw_handle(), &operation, &mut count, 1); }
                return Err(io::Error::new(if result == WAIT_TIMEOUT { io::ErrorKind::TimedOut } else { io::ErrorKind::BrokenPipe }, "Control operation retired"));
            }
        }
        if unsafe { GetOverlappedResult(self.pipe.as_raw_handle(), &operation, &mut count, 1) } == 0 { return Err(io::Error::last_os_error()); }
        if unsafe { WaitForSingleObject(self.server.as_raw_handle(), 0) } != WAIT_TIMEOUT {
            return Err(io::Error::new(io::ErrorKind::BrokenPipe, "Control service exited"));
        }
        if count == 0 || count as usize > buffer.len() { return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "Control closed")); }
        Ok(count as usize)
    }

    fn exact(&self, mut buffer: &mut [u8], write: bool, timeout_ms: u32) -> io::Result<()> {
        let deadline = (timeout_ms != INFINITE).then(|| Instant::now() + Duration::from_millis(u64::from(timeout_ms)));
        while !buffer.is_empty() {
            let remaining = match deadline {
                Some(deadline) => deadline.checked_duration_since(Instant::now()).ok_or_else(|| io::Error::new(io::ErrorKind::TimedOut, "Control deadline"))?.as_millis().max(1).min(u128::from(u32::MAX - 1)) as u32,
                None => INFINITE,
            };
            let count = self.transfer(buffer, write, remaining)?; buffer = &mut buffer[count..];
        }
        Ok(())
    }
    fn read(&self, timeout_ms: u32) -> io::Result<(u8, Vec<u8>)> {
        let mut header = [0; 4]; self.exact(&mut header, false, timeout_ms)?;
        let length = u32::from_be_bytes(header) as usize;
        if !(1..=MAX_FRAME).contains(&length) { return Err(invalid()); }
        let mut body = vec![0; length]; self.exact(&mut body, false, timeout_ms.min(3000))?;
        Ok((body[0], body[1..].to_vec()))
    }
    fn write(&self, kind: u8, body: &[u8]) -> io::Result<()> {
        if body.len() >= MAX_FRAME { return Err(invalid()); }
        let mut record = Vec::with_capacity(body.len() + 5);
        record.extend_from_slice(&((body.len() + 1) as u32).to_be_bytes()); record.push(kind); record.extend_from_slice(body);
        self.exact(&mut record, true, 3000)
    }
}

unsafe fn require_descriptor(pipe: HANDLE) -> io::Result<()> {
    let mut descriptor = null_mut();
    let error = GetSecurityInfo(pipe, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        null_mut(), null_mut(), null_mut(), null_mut(), &mut descriptor);
    if error != 0 { return Err(io::Error::from_raw_os_error(error as i32)); }
    let result = (|| {
        if descriptor.is_null() { return Err(invalid()); }
        let length = GetSecurityDescriptorLength(descriptor) as usize;
        if !(20..=65536).contains(&length) { return Err(invalid()); }
        require_pipe_descriptor(std::slice::from_raw_parts(descriptor.cast(), length))
    })();
    LocalFree(descriptor); result
}

unsafe fn require_system(process: HANDLE) -> io::Result<()> {
    let mut raw = null_mut();
    if OpenProcessToken(process, TOKEN_QUERY, &mut raw) == 0 { return Err(io::Error::last_os_error()); }
    let token = handle(raw)?;
    let mut size = 0;
    GetTokenInformation(token.as_raw_handle(), TokenUser, null_mut(), 0, &mut size);
    if !(16..=65536).contains(&size) { return Err(invalid()); }
    let mut data = vec![0u8; size as usize];
    if GetTokenInformation(token.as_raw_handle(), TokenUser, data.as_mut_ptr().cast(), size, &mut size) == 0 { return Err(io::Error::last_os_error()); }
    let user = std::ptr::read_unaligned(data.as_ptr().cast::<PSID>());
    let offset = (user as usize).checked_sub(data.as_ptr() as usize).ok_or_else(invalid)?;
    if sid(&data, offset)? != SYSTEM_SID { return Err(invalid()); }
    Ok(())
}

/// N2 authorization projection. N3/N4 supply actual TLS/source observations;
/// this role still opens no endpoint, DLL, key file or media socket.
pub fn run(registration: [u8; 16]) -> io::Result<()> {
    let control = Control::connect(registration)?;
    let (kind, hello) = control.read(3000)?;
    if kind != HELLO { return Err(invalid()); }
    require_hello(&hello, &registration, control.server_pid, control.server_created)?;
    control.write(READY, &hello)?;
    let mut authority=crate::audio_authority::AudioAuthority::new(hello[17..49].try_into().unwrap(),registration)?;
    fn now()->u64 {let mut value=0;unsafe{QueryInterruptTimePrecise(&mut value);}value}
    loop {
        let timeout=match authority.deadline() {
            Some(until)=> {
                let remaining=until.checked_sub(now()).ok_or_else(invalid)?;
                ((remaining/10000)+u64::from(remaining%10000!=0)).max(1).min(u64::from(u32::MAX-1)) as u32
            }, None=>INFINITE,
        };
        let (kind,body)=control.read(timeout)?;
        if kind==STOP && body.is_empty() {authority.close();return Ok(());}
        if kind!=AUTHORITY {return Err(invalid());}
        let grant=crate::audio_authority::ControlGrant::parse(&body)?;let closed=grant.closed;
        authority.apply(grant,now())?;
        control.write(AUTHORITY_OBSERVED,&body)?;
        if closed {return Ok(());}
    }
}
