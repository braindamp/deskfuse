//! Concrete source/delivery cutoff guard. No allocations, locks, signatures or
//! persistence in media_allowed(). Input comes only from the bound SYSTEM pipe.
use std::io;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ControlGrant {
    pub host: [u8;32], pub namespace: [u8;16], pub enrollment: [u8;16], pub boot: [u8;32], pub worker: [u8;16],
    pub source: [u8;16], pub source_generation: u64, pub source_digest: [u8;32], pub binding: [u8;32],
    pub delivery_generation: u64, pub delivery_digest: [u8;32], pub revision: u64, pub until: u64,
    pub output_generation: u64, pub ready: bool, pub closed: bool,
}
fn invalid() -> io::Error { io::Error::new(io::ErrorKind::PermissionDenied, "Stale or invalid audio authority") }
impl ControlGrant {
    pub fn parse(bytes: &[u8]) -> io::Result<Self> {
        struct Reader<'a>(&'a [u8]);
        impl Reader<'_> {
            fn take<const N: usize>(&mut self) -> io::Result<[u8;N]> {
                if self.0.len() < N { return Err(invalid()); }
                let (a,b) = self.0.split_at(N); self.0 = b; Ok(a.try_into().unwrap())
            }
            fn u64(&mut self) -> io::Result<u64> { Ok(u64::from_be_bytes(self.take()?)) }
            fn boolean(&mut self) -> io::Result<bool> { match self.take::<1>()?[0] { 0 => Ok(false), 1 => Ok(true), _ => Err(invalid()) } }
        }
        let domain = b"DeskFuse.Audio.Control.v1\0";
        if bytes.len() > 2048 || !bytes.starts_with(domain) { return Err(invalid()); }
        let mut r = Reader(&bytes[domain.len()..]);
        let g = Self { host:r.take()?, namespace:r.take()?, enrollment:r.take()?, boot:r.take()?, worker:r.take()?, source:r.take()?,
            source_generation:r.u64()?, source_digest:r.take()?, binding:r.take()?, delivery_generation:r.u64()?, delivery_digest:r.take()?,
            revision:r.u64()?, until:r.u64()?, output_generation:r.u64()?, ready:r.boolean()?, closed:r.boolean()? };
        if !r.0.is_empty() {return Err(invalid());} g.validate()?;
        Ok(g)
    }
    fn validate(&self) -> io::Result<()> {
        if self.host == [0;32] || self.namespace == [0;16] || self.enrollment == [0;16] || self.boot == [0;32] ||
            self.worker == [0;16] || self.source == [0;16] || self.source_generation == 0 || self.source_digest == [0;32] || self.revision == 0 ||
            !self.closed && self.until == 0 || (self.delivery_generation == 0) != (self.delivery_digest == [0;32]) ||
            self.delivery_generation != 0 && self.binding == [0;32] || self.ready && (self.closed || self.delivery_generation == 0 || self.output_generation == 0) {
            return Err(invalid());
        } Ok(())
    }
    fn same_source(&self, other: &Self) -> bool {
        self.host == other.host && self.namespace == other.namespace && self.enrollment == other.enrollment &&
        self.boot == other.boot && self.worker == other.worker && self.source == other.source &&
        self.source_generation == other.source_generation && self.source_digest == other.source_digest
    }
}

/// One source incarnation. Replace this guard only after its actual source and
/// kernel/transport owners have joined; a new cutoff never revives a terminal one.
pub struct AudioAuthority {
    boot: [u8;32], worker: [u8;16], current: Option<ControlGrant>, terminal: bool, last_now: u64,
    highest_delivery: u64, source_observed: bool, delivery_observed: bool, output_observed: u64,
}
impl AudioAuthority {
    pub fn new(boot: [u8;32], worker: [u8;16]) -> io::Result<Self> {
        if boot == [0;32] || worker == [0;16] { return Err(invalid()); }
        Ok(Self { boot,worker,current:None,terminal:false,last_now:0,highest_delivery:0,
            source_observed:false,delivery_observed:false,output_observed:0 })
    }
    pub fn close(&mut self) { self.terminal=true; self.delivery_observed=false; self.output_observed=0; }
    fn advance(&mut self, now: u64) -> bool {
        if now < self.last_now || self.current.as_ref().is_some_and(|g| now >= g.until) { self.close(); }
        self.last_now = self.last_now.max(now); !self.terminal
    }
    pub fn deadline(&self) -> Option<u64> { if self.terminal { None } else { self.current.as_ref().map(|g|g.until) } }
    pub fn revision(&self) -> u64 { self.current.as_ref().map_or(0, |g|g.revision) }
    pub fn source_allowed(&mut self, now: u64) -> bool { self.advance(now) && self.current.is_some() }
    pub fn apply(&mut self, grant: ControlGrant, now: u64) -> io::Result<bool> {
        let result=self.apply_inner(grant,now); if result.is_err() {self.close();} result
    }
    fn apply_inner(&mut self, grant: ControlGrant, now: u64) -> io::Result<bool> {
        grant.validate()?;
        if !self.advance(now) || grant.boot != self.boot || grant.worker != self.worker || !grant.closed && now >= grant.until { return Err(invalid()); }
        if let Some(old)=self.current.as_ref() {
            if *old == grant { return Ok(false); } // Exact retries never recompute a deadline or reset local readiness.
            if !old.same_source(&grant) || grant.revision <= old.revision || old.binding != [0;32] && old.binding != grant.binding { return Err(invalid()); }
            if grant.delivery_generation == old.delivery_generation {
                if grant.delivery_digest != old.delivery_digest || grant.output_generation < old.output_generation ||
                    grant.delivery_generation == 0 && grant.until > old.until { return Err(invalid()); }
            } else {
                if grant.delivery_generation != 0 && grant.delivery_generation <= self.highest_delivery ||
                    grant.delivery_generation == 0 && grant.until > old.until { return Err(invalid()); }
                self.delivery_observed=false; self.output_observed=0;
            }
        }
        if self.current.as_ref().is_none_or(|old| old.binding != grant.binding) { self.source_observed=false; }
        self.highest_delivery=self.highest_delivery.max(grant.delivery_generation);
        if !grant.ready {self.output_observed=0;}
        if grant.closed { self.close(); }
        self.current=Some(grant); Ok(true)
    }
    /// N4 calls this from its actual observed source-binding completion.
    pub fn source_observed(&mut self, binding: [u8;32], now: u64) -> io::Result<()> {
        if !self.advance(now) || binding == [0;32] || self.current.as_ref().is_none_or(|g|g.binding != binding) {self.close();return Err(invalid());}
        self.source_observed=true; Ok(())
    }
    /// N3 calls this after exact approved TLS/connection binding, never on a CID/IP alone.
    pub fn delivery_observed(&mut self, generation: u64, digest: [u8;32], now: u64) -> io::Result<()> {
        if !self.advance(now) || generation == 0 || self.current.as_ref().is_none_or(|g|g.delivery_generation != generation || g.delivery_digest != digest) {
            self.close();return Err(invalid());
        }
        self.delivery_observed=true; Ok(())
    }
    pub fn output_observed(&mut self, generation: u64, output: u64, now: u64) -> io::Result<()> {
        if !self.advance(now) || !self.delivery_observed || output == 0 || self.current.as_ref().is_none_or(|g|
            g.delivery_generation != generation || g.output_generation != output) {return Err(invalid());}
        self.output_observed=output; Ok(())
    }
    pub fn media_allowed(&mut self, now: u64) -> bool {
        self.advance(now) && self.source_observed && self.delivery_observed && self.current.as_ref().is_some_and(|g|
            g.ready && g.delivery_generation != 0 && g.output_generation != 0 && self.output_observed == g.output_generation)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn vector() -> (Vec<u8>,ControlGrant) {
        let fixture:serde_json::Value=serde_json::from_str(include_str!("../../../../tools/protocol3/audio-authority-v1.json")).unwrap();
        let hex=fixture["controlHex"].as_str().unwrap();
        let bytes=(0..hex.len()).step_by(2).map(|i|u8::from_str_radix(&hex[i..i+2],16).unwrap()).collect::<Vec<_>>();
        let g=ControlGrant::parse(&bytes).unwrap(); (bytes,g)
    }
    #[test]
    fn typed_vector_and_local_evidence_are_required_before_media() {
        let (bytes,g)=vector(); assert_eq!((g.source_generation,g.delivery_generation,g.revision,g.until),(7,10,12,1000));
        let mut owner=AudioAuthority::new([4;32],[5;16]).unwrap(); owner.apply(g.clone(),100).unwrap();
        assert!(!owner.media_allowed(100)); owner.source_observed([9;32],100).unwrap();
        owner.delivery_observed(10,[11;32],100).unwrap(); owner.output_observed(10,13,100).unwrap(); assert!(owner.media_allowed(100));
        assert!(!owner.apply(g.clone(),101).unwrap()); assert!(owner.media_allowed(101));
        let mut renewed=g; renewed.revision=13; renewed.until=1200; owner.apply(renewed.clone(),102).unwrap();
        assert!(owner.media_allowed(1199)); assert!(!owner.media_allowed(1200));
        renewed.revision=14; renewed.until=2000; assert!(owner.apply(renewed,1201).is_err());
        for end in 0..bytes.len() {assert!(ControlGrant::parse(&bytes[..end]).is_err());}
        let mut trailing=bytes.clone(); trailing.push(0); assert!(ControlGrant::parse(&trailing).is_err());
    }
    #[test]
    fn disconnect_grace_stale_context_and_counter_wrap_cannot_reactivate_a_source() {
        let (_,g)=vector();
        for which in ["boot","source","generation","binding","revision"] {
            let mut owner=AudioAuthority::new([4;32],[5;16]).unwrap();owner.apply(g.clone(),100).unwrap();
            let mut bad=g.clone();bad.revision=13;
            match which {"boot"=>bad.boot[0]^=1,"source"=>bad.source[0]^=1,"generation"=>bad.source_generation+=1,
                "binding"=>bad.binding[0]^=1,_=>bad.revision=0}
            assert!(owner.apply(bad,101).is_err());assert!(!owner.media_allowed(101));
        }
        let mut owner=AudioAuthority::new([4;32],[5;16]).unwrap();owner.apply(g.clone(),100).unwrap();
        let mut detached=g.clone();detached.revision=13;detached.delivery_generation=0;detached.delivery_digest=[0;32];
        detached.output_generation=0;detached.ready=false;detached.until=500;owner.apply(detached.clone(),200).unwrap();
        let mut resumed=g.clone();resumed.revision=14;resumed.delivery_generation=11;resumed.delivery_digest=[12;32];
        resumed.output_generation=0;resumed.ready=false;resumed.until=900;owner.apply(resumed.clone(),300).unwrap();
        assert!(!owner.media_allowed(300));
        let mut stale=g;stale.revision=15;assert!(owner.apply(stale,301).is_err());
        let mut owner=AudioAuthority::new([4;32],[5;16]).unwrap();owner.apply(detached,200).unwrap();
        assert!(!owner.media_allowed(500));assert!(owner.apply(resumed,501).is_err());
    }
}
