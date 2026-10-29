//! Cold product control. No PCM, arbitrary command paths, reconnect or v1 trust.
use std::ffi::OsString;
use std::io;

pub const MAX_FRAME: usize = 8192;
pub const HELLO: u8 = 1;
pub const READY: u8 = 2;
pub const STOP: u8 = 3;
pub const HELLO_BYTES: usize = 61;
const CLIENT_MASK: u32 = 0x0012_0083;
const SYSTEM_SID: &[u8] = &[1, 1, 0, 0, 0, 0, 0, 5, 18, 0, 0, 0];

fn invalid() -> io::Error { io::Error::new(io::ErrorKind::InvalidData, "Invalid broker control binding") }

pub fn registration(args: impl IntoIterator<Item = OsString>) -> io::Result<[u8; 16]> {
    let mut args = args.into_iter();
    let value = args.next().ok_or_else(invalid)?;
    if args.next().is_some() { return Err(invalid()); }
    let value = value.to_str().ok_or_else(invalid)?;
    let hex = value.strip_prefix("--deskfuse-broker-registration=").ok_or_else(invalid)?;
    if hex.len() != 32 { return Err(invalid()); }
    let mut id = [0u8; 16];
    fn nibble(value: u8) -> io::Result<u8> {
        match value { b'0'..=b'9' => Ok(value - b'0'), b'a'..=b'f' => Ok(value - b'a' + 10), _ => Err(invalid()) }
    }
    for (at, chunk) in hex.as_bytes().chunks_exact(2).enumerate() {
        id[at] = (nibble(chunk[0])? << 4) | nibble(chunk[1])?;
    }
    if id == [0; 16] { return Err(invalid()); }
    Ok(id)
}

pub fn require_hello(bytes: &[u8], registration: &[u8; 16], server_pid: u32, server_created: u64) -> io::Result<()> {
    if bytes.len() != HELLO_BYTES || bytes[0] != 1 || bytes[1..17] != *registration ||
        bytes[17..49].iter().all(|b| *b == 0) ||
        u32::from_be_bytes(bytes[49..53].try_into().unwrap()) != server_pid || server_pid == 0 ||
        u64::from_be_bytes(bytes[53..61].try_into().unwrap()) != server_created || server_created == 0 {
        return Err(invalid());
    }
    Ok(())
}

fn sid(bytes: &[u8], offset: usize) -> io::Result<&[u8]> {
    let header = bytes.get(offset..offset.checked_add(8).ok_or_else(invalid)?).ok_or_else(invalid)?;
    if header[0] != 1 || header[1] > 15 { return Err(invalid()); }
    bytes.get(offset..offset + 8 + usize::from(header[1]) * 4).ok_or_else(invalid)
}

/// Validate a bounded self-relative descriptor returned by GetSecurityInfo.
/// Allows client data/read-control rights, never another untrusted server.
pub fn require_pipe_descriptor(bytes: &[u8]) -> io::Result<()> {
    if !(20..=65536).contains(&bytes.len()) || bytes[0] != 1 { return Err(invalid()); }
    let control = u16::from_le_bytes(bytes[2..4].try_into().unwrap());
    if control & 0x9004 != 0x9004 { return Err(invalid()); } // self-relative, protected, DACL present
    let owner = u32::from_le_bytes(bytes[4..8].try_into().unwrap()) as usize;
    if owner < 20 || sid(bytes, owner)? != SYSTEM_SID { return Err(invalid()); }
    let offset = u32::from_le_bytes(bytes[16..20].try_into().unwrap()) as usize;
    if offset < 20 { return Err(invalid()); }
    let acl = bytes.get(offset..).ok_or_else(invalid)?;
    if acl.len() < 8 || !matches!(acl[0], 2 | 4) { return Err(invalid()); }
    let size = usize::from(u16::from_le_bytes(acl[2..4].try_into().unwrap()));
    let acl = acl.get(..size).filter(|a| a.len() >= 8).ok_or_else(invalid)?;
    let count = u16::from_le_bytes(acl[4..6].try_into().unwrap());
    let mut cursor = 8;
    for _ in 0..count {
        let ace = acl.get(cursor..).filter(|a| a.len() >= 8).ok_or_else(invalid)?;
        let length = usize::from(u16::from_le_bytes(ace[2..4].try_into().unwrap()));
        let ace = ace.get(..length).filter(|a| a.len() >= 16).ok_or_else(invalid)?;
        if !matches!(ace[0], 0 | 1) || ace[1] & 0x08 != 0 { return Err(invalid()); } // no callbacks/INHERIT_ONLY
        let trustee = sid(ace, 8)?;
        if trustee.len() + 8 != length { return Err(invalid()); }
        let access = u32::from_le_bytes(ace[4..8].try_into().unwrap());
        if ace[0] == 0 && trustee != SYSTEM_SID && access & !CLIENT_MASK != 0 { return Err(invalid()); }
        cursor += length;
    }
    if cursor > size { return Err(invalid()); }
    Ok(())
}

#[cfg(windows)]
pub mod windows;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn registration_is_exact_generated_arg_only() {
        let arg = "--deskfuse-broker-registration=00112233445566778899aabbccddeeff";
        assert_eq!(registration([arg.into()]).unwrap(), [0,17,34,51,68,85,102,119,136,153,170,187,204,221,238,255]);
        for bad in ["", "--session-stdin", "--deskfuse-broker-registration=00000000000000000000000000000000",
            "--deskfuse-broker-registration=00112233445566778899AABBCCDDEEFF", "--deskfuse-broker-registration=00112233-4455-6677-8899-aabbccddeeff"] {
            assert!(registration([bad.into()]).is_err());
        }
        assert!(registration([arg.into(), "--key=untrusted".into()]).is_err());
    }
    #[test]
    fn bootstrap_binds_registration_boot_and_retained_service_incarnation() {
        let fixture: serde_json::Value = serde_json::from_str(include_str!("../../../../tools/protocol3/audio-worker-control-v1.json")).unwrap();
        let decode = |value: &str| (0..value.len()).step_by(2).map(|at| u8::from_str_radix(&value[at..at+2], 16).unwrap()).collect::<Vec<_>>();
        let canonical = decode(fixture["helloHex"].as_str().unwrap());
        assert!(require_hello(&canonical, &[0,17,34,51,68,85,102,119,136,153,170,187,204,221,238,255], 17, 31).is_ok());
        assert!(require_pipe_descriptor(&decode(fixture["pipeDescriptorHex"].as_str().unwrap())).is_ok());
        let mut hello = [0u8; HELLO_BYTES]; hello[0] = 1; hello[1..17].fill(2); hello[17..49].fill(3);
        hello[49..53].copy_from_slice(&17u32.to_be_bytes()); hello[53..].copy_from_slice(&31u64.to_be_bytes());
        assert!(require_hello(&hello, &[2;16], 17, 31).is_ok());
        assert!(require_hello(&hello, &[4;16], 17, 31).is_err());
        assert!(require_hello(&hello, &[2;16], 17, 32).is_err());
        hello[17..49].fill(0); assert!(require_hello(&hello, &[2;16], 17, 31).is_err());
    }
    fn descriptor(access: u32) -> Vec<u8> {
        let mut bytes = vec![0u8; 60]; bytes[0] = 1; bytes[2..4].copy_from_slice(&0x9004u16.to_le_bytes());
        bytes[4..8].copy_from_slice(&20u32.to_le_bytes()); bytes[16..20].copy_from_slice(&32u32.to_le_bytes());
        bytes[20..32].copy_from_slice(SYSTEM_SID); bytes[32] = 2;
        bytes[34..36].copy_from_slice(&28u16.to_le_bytes()); bytes[36..38].copy_from_slice(&1u16.to_le_bytes());
        bytes[42..44].copy_from_slice(&20u16.to_le_bytes()); bytes[44..48].copy_from_slice(&access.to_le_bytes());
        bytes[48..60].copy_from_slice(&[1,1,0,0,0,0,0,5,11,0,0,0]); bytes
    }
    #[test]
    fn pipe_security_rejects_squatting_rights_null_acl_and_malformed_records() {
        let valid = descriptor(CLIENT_MASK); assert!(require_pipe_descriptor(&valid).is_ok());
        for mask in [CLIENT_MASK | 4, CLIENT_MASK | 0x40000, CLIENT_MASK | 0x40000000] {
            assert!(require_pipe_descriptor(&descriptor(mask)).is_err());
        }
        for length in 0..valid.len() { assert!(require_pipe_descriptor(&valid[..length]).is_err()); }
        let mut invalid = valid.clone(); invalid[16..20].fill(0); assert!(require_pipe_descriptor(&invalid).is_err());
        let mut invalid = valid; invalid[28] = 19; assert!(require_pipe_descriptor(&invalid).is_err());
    }
}
