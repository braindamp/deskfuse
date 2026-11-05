use super::*;
use crate::audio_authority::ControlGrant;
use crate::audio_control::{Reader, SourceScope};
use crate::committed_source::{OpenedPeriod, Source};
use std::sync::Arc;
use std::thread::JoinHandle;

#[cfg(test)]
pub(super) type TestOpen = Box<dyn FnOnce(HANDLE) -> io::Result<OpenedPeriod> + Send>;

pub(super) fn envelope(body: &[u8]) -> io::Result<(ControlGrant, &[u8], Reader<'_>)> {
    let mut reader = Reader(body);
    if reader.byte()? != 1 { return Err(invalid()); }
    let size = reader.u16()? as usize;
    let grant = ControlGrant::parse(reader.bytes(size)?)?;
    if grant.closed || grant.ready || grant.binding != [0; 32]
        || grant.delivery_generation != 0 || grant.output_generation != 0 {
        return Err(invalid());
    }
    let prefix = &body[..size + 3];
    Ok((grant, prefix, reader))
}

pub(super) fn endpoint(mut reader: Reader<'_>) -> io::Result<&str> {
    let size = reader.u16()? as usize;
    let value = std::str::from_utf8(reader.bytes(size)?).map_err(|_| invalid())?;
    reader.end()?;
    if value.is_empty() || size > 1024 || value.chars().any(char::is_control) { return Err(invalid()); }
    Ok(value)
}

pub(super) fn pcm_handles(mut reader: Reader<'_>) -> io::Result<[u64; 4]> {
    let values = [reader.u64()?, reader.u64()?, reader.u64()?, reader.u64()?];
    reader.end()?;
    let [file, section, event, generation] = values;
    if generation == 0 || [file, section, event].iter().any(|&h| h == 0 || h > isize::MAX as u64)
        || file == section || file == event || section == event { return Err(invalid()); }
    Ok(values)
}

pub(super) struct Preparation {
    pub(super) grant: ControlGrant,
    pub(super) scope: SourceScope,
    prefix: Vec<u8>,
    endpoint: String,
    cancel: Arc<OwnedHandle>,
    task: Option<JoinHandle<io::Result<OpenedPeriod>>>,
    period: Option<OpenedPeriod>,
}
impl Preparation {
    pub(super) fn cancel(&self) { unsafe { SetEvent(self.cancel.as_raw_handle()); } }
    pub(super) fn start(grant: ControlGrant, scope: SourceScope, prefix: &[u8], endpoint: &str,
        #[cfg(test)] test_open: Option<TestOpen>,
    ) -> io::Result<Self> {
        let cancel = Arc::new(handle(unsafe { CreateEventW(null(), 1, 0, null()) })?);
        let flag = Arc::clone(&cancel);
        let name = endpoint.to_owned();
        let until = grant.until;
        let minimum = scope.minimum_period;
        let task = std::thread::Builder::new().name("committed-source-setup".into()).spawn(move || {
            #[cfg(test)]
            if let Some(open) = test_open { return open(flag.as_raw_handle()); }
            Source::open_prepared(&name, minimum, flag.as_raw_handle(), until)
        })?;
        Ok(Self { grant, scope, prefix: prefix.to_vec(), endpoint: endpoint.to_owned(),
            cancel, task: Some(task), period: None })
    }
    pub(super) fn completion(&self) -> Option<std::os::windows::io::BorrowedHandle<'_>> {
        use std::os::windows::io::AsHandle;
        self.task.as_ref().map(|task| task.as_handle())
    }
    pub(super) fn opened(&mut self) -> io::Result<Option<Vec<u8>>> {
        if !self.task.as_ref().is_some_and(JoinHandle::is_finished) { return Ok(None); }
        let period = self.task.take().unwrap().join().map_err(|_| invalid())??;
        let (source, abi, build, frames) = period.facts();
        let mut body = self.prefix.clone();
        body.extend_from_slice(&source);
        body.extend_from_slice(&abi.to_be_bytes());
        body.extend_from_slice(&build.to_be_bytes());
        body.extend_from_slice(&1u64.to_be_bytes()); // One period incarnation per fixed worker.
        body.extend_from_slice(&frames.to_be_bytes());
        self.period = Some(period);
        Ok(Some(body))
    }
    pub(super) fn map(mut self, handles: [u64; 4]) -> io::Result<(Source, Vec<u8>, [u8; 32])> {
        let period = self.period.take().ok_or_else(invalid)?;
        let (source, abi, build, _) = period.facts();
        let mapped = Source::map_prepared(period, handles)?;
        let mut binding = b"DeskFuse.Audio.Binding.v1\0".to_vec();
        binding.extend_from_slice(&self.scope.digest);
        binding.extend_from_slice(&self.scope.device_key);
        binding.extend_from_slice(&(self.endpoint.len() as u16).to_be_bytes());
        binding.extend_from_slice(self.endpoint.as_bytes());
        binding.extend_from_slice(&source);
        binding.extend_from_slice(&abi.to_be_bytes());
        binding.extend_from_slice(&build.to_be_bytes());
        binding.extend_from_slice(&1u64.to_be_bytes());
        binding.extend_from_slice(&handles[3].to_be_bytes());
        let digest = boring::sha::sha256(&binding);
        let mut body = self.prefix.clone();
        body.extend_from_slice(&binding);
        Ok((mapped, body, digest))
    }
}
impl Drop for Preparation {
    fn drop(&mut self) {
        self.cancel();
        if let Some(task) = self.task.take() { let _ = task.join(); }
    }
}
