//! One native connection/key incarnation. Setup is cold work; the future owner
//! transfers this value once, after handshake, to its QUIC service loop.
//! No source is opened and no media API is exposed by this control increment.
use crate::{
    audio_authority::ControlGrant,
    audio_control::{
        self as wire, Decoder, DeliveryBinding, Observation, Record, SourceScope, MAX_PENDING,
    },
    audio_tls::{require_established, Identity, Role},
    reactor::Egress,
};
use boring::rand::rand_bytes;
use std::{
    collections::{HashMap, VecDeque},
    io,
    net::{SocketAddr, UdpSocket},
};
fn denied() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "Audio delivery context refused",
    )
}
fn crypto<E>(_: E) -> io::Error {
    denied()
}
fn random<const N: usize>() -> io::Result<[u8; N]> {
    let mut b = [0; N];
    rand_bytes(&mut b).map_err(crypto)?;
    if b == [0; N] {
        return Err(denied());
    }
    Ok(b)
}

/// Consumed into one connection. No private-key import, export, path or session
/// resumption API exists. Public certificate bytes are inspection facts only.
pub struct DeliveryTls {
    identity: Identity,
    role: Role,
}
impl DeliveryTls {
    pub fn generate_for_mode(role: Role, mode: u8) -> io::Result<Self> {
        wire::DeliveryMode::require_supported(mode)?;
        Self::generate(role)
    }
    pub fn generate(role: Role) -> io::Result<Self> {
        Ok(Self {
            identity: Identity::generate(role)?,
            role,
        })
    }
    pub fn connect(
        self,
        local: SocketAddr,
        peer: SocketAddr,
        provisional_until100ns: u64,
        idle_ms: u32,
    ) -> io::Result<ControlConnection> {
        if self.role != Role::Phone || idle_ms == 0 {
            return Err(denied());
        }
        let (mut config, der, hash) = self.identity.config(self.role)?;
        config.set_max_idle_timeout(u64::from(idle_ms));
        let id = random::<16>()?;
        let id = quiche::ConnectionId::from_ref(&id);
        let conn = quiche::connect(None, &id, local, peer, &mut config).map_err(crypto)?;
        ControlConnection::new(
            conn,
            self.role,
            local,
            peer,
            der,
            hash,
            provisional_until100ns,
        )
    }
    pub fn accept(
        self,
        packet: &mut [u8],
        info: quiche::RecvInfo,
        provisional_until100ns: u64,
        idle_ms: u32,
    ) -> io::Result<ControlConnection> {
        if self.role != Role::Native || idle_ms == 0 || packet.len() < 1200 || packet.len() > 1350 {
            return Err(denied());
        }
        let h = quiche::Header::from_slice(packet, quiche::MAX_CONN_ID_LEN).map_err(crypto)?;
        if h.ty != quiche::Type::Initial || h.version != quiche::PROTOCOL_VERSION {
            return Err(denied());
        }
        let (mut config, der, hash) = self.identity.config(self.role)?;
        config.set_max_idle_timeout(u64::from(idle_ms));
        let mut conn =
            quiche::accept(&h.dcid, None, info.to, info.from, &mut config).map_err(crypto)?;
        conn.recv(packet, info).map_err(crypto)?;
        ControlConnection::new(
            conn,
            self.role,
            info.to,
            info.from,
            der,
            hash,
            provisional_until100ns,
        )
    }
}
#[derive(Clone, Debug)]
pub struct TlsFacts {
    pub local_certificate: Vec<u8>,
    pub peer_certificate: Vec<u8>,
    pub local_hash: [u8; 32],
    pub peer_hash: [u8; 32],
}
struct Approved {
    binding: DeliveryBinding,
    boot: [u8; 32],
    revision: u64,
    until: u64,
}
struct QueuedControl {
    bytes: Vec<u8>,
    offset: usize,
    negative: bool,
}
pub struct ControlConnection {
    conn: quiche::Connection,
    role: Role,
    local: SocketAddr,
    peer: SocketAddr,
    local_cert: Vec<u8>,
    local_hash: [u8; 32],
    facts: Option<TlsFacts>,
    observation: Option<Observation>,
    id: [u8; 16],
    nonce: [u8; 32],
    deadline: u64,
    last_now: u64,
    retired: bool,
    approved: Option<Approved>,
    decoder: Decoder,
    read_backlog: VecDeque<Record>,
    writes: VecDeque<QueuedControl>,
    pending: HashMap<u64, u8>,
    last_request: u64,
    egress: Egress,
}
impl ControlConnection {
    fn new(
        conn: quiche::Connection,
        role: Role,
        local: SocketAddr,
        peer: SocketAddr,
        local_cert: Vec<u8>,
        local_hash: [u8; 32],
        deadline: u64,
    ) -> io::Result<Self> {
        if deadline == 0 {
            return Err(denied());
        }
        Ok(Self {
            conn,
            role,
            local,
            peer,
            local_cert,
            local_hash,
            facts: None,
            observation: None,
            id: random()?,
            nonce: random()?,
            deadline,
            last_now: 0,
            retired: false,
            approved: None,
            decoder: Decoder::default(),
            read_backlog: VecDeque::new(),
            writes: VecDeque::new(),
            pending: HashMap::new(),
            last_request: 0,
            egress: Egress::default(),
        })
    }
    fn advance(&mut self, now: u64) -> io::Result<()> {
        if self.retired || now < self.last_now || now >= self.deadline || self.conn.is_closed() {
            self.retire();
            return Err(denied());
        }
        self.last_now = now;
        if self.facts.is_none() && self.conn.is_established() {
            let hash = match require_established(&self.conn, self.role) {
                Ok(h) => h,
                Err(e) => {
                    self.retire();
                    return Err(e);
                }
            };
            self.facts = Some(TlsFacts {
                local_certificate: self.local_cert.clone(),
                peer_certificate: self.conn.peer_cert().ok_or_else(denied)?.to_vec(),
                local_hash: self.local_hash,
                peer_hash: hash,
            });
            if self.role == Role::Native {
                let observation = Observation {
                    connection: self.id,
                    native: self.local_hash,
                    phone: hash,
                    nonce: self.nonce,
                };
                self.writes.push_back(QueuedControl {
                    bytes: Record {
                        kind: wire::NOTICE,
                        request: 0,
                        body: observation.encode()?,
                    }
                    .encode()?,
                    offset: 0,
                    negative: false,
                });
                self.observation = Some(observation);
            }
        }
        Ok(())
    }
    pub fn facts(&mut self, now: u64) -> io::Result<Option<TlsFacts>> {
        self.advance(now)?;
        Ok(self.facts.clone())
    }
    pub fn observation(&mut self, now: u64) -> io::Result<Option<Observation>> {
        self.advance(now)?;
        Ok(self.observation.clone())
    }
    pub fn receive(
        &mut self,
        packet: &mut [u8],
        info: quiche::RecvInfo,
        now: u64,
    ) -> io::Result<bool> {
        self.advance(now)?;
        // Ignore injected/wrong-path packets without killing a healthy delivery.
        // Destination policy 1 never authorizes migration or NAT rebinding.
        if info.from != self.peer || info.to != self.local {
            return Ok(false);
        }
        if packet.len() > 1350 {
            return Err(wire::invalid());
        }
        match self.conn.recv(packet, info) {
            Ok(_) => {}
            Err(quiche::Error::Done) => return Ok(false),
            Err(_) => {
                self.retire();
                return Err(denied());
            }
        }
        self.advance(now)?;
        Ok(true)
    }
    pub fn read_control(&mut self, now: u64) -> io::Result<Vec<Record>> {
        self.advance(now)?;
        if self.facts.is_none() {
            return Ok(vec![]);
        }
        let mut result = Vec::with_capacity(MAX_PENDING);
        while result.len() < MAX_PENDING {
            if let Some(record) = self.read_backlog.pop_front() {
                result.push(record);
                continue;
            }
            let ids = self.conn.readable().collect::<Vec<_>>();
            if ids.iter().any(|id| *id != 0) {
                self.retire();
                return Err(wire::invalid());
            }
            if ids.is_empty() {
                break;
            }
            let mut bytes = [0u8; 1024];
            let (n, fin) = match self.conn.stream_recv(0, &mut bytes) {
                Ok(v) => v,
                Err(quiche::Error::Done) => break,
                Err(_) => {
                    self.retire();
                    return Err(denied());
                }
            };
            if fin {
                self.retire();
                return Err(denied());
            }
            let mut at = 0;
            while at < n {
                let (used, record) = match self.decoder.push(&bytes[at..n]) {
                    Ok(v) => v,
                    Err(e) => {
                        self.retire();
                        return Err(e);
                    }
                };
                if used == 0 {
                    self.retire();
                    return Err(wire::invalid());
                }
                at += used;
                if let Some(record) = record {
                    if let Err(e) = self.check_record(&record) {
                        self.retire();
                        return Err(e);
                    }
                    if result.len() < MAX_PENDING {
                        result.push(record)
                    } else if self.read_backlog.len() < MAX_PENDING {
                        self.read_backlog.push_back(record)
                    } else {
                        self.retire();
                        return Err(wire::invalid());
                    }
                }
            }
            if n == 0 {
                break;
            }
        }
        Ok(result)
    }
    fn check_record(&mut self, r: &Record) -> io::Result<()> {
        if self.role == Role::Native {
            if !(wire::INFO..=wire::APPLIED_RESULT).contains(&r.kind)
                || self.last_request == u64::MAX
                || r.request != self.last_request + 1
                || !self.pending_capacity(r.kind)
            {
                return Err(wire::invalid());
            }
            let n = r.body.len();
            let valid = match r.kind {
                wire::INFO => n == 0,
                wire::PREPARE => n == 49,
                wire::ISSUE => n == 53,
                wire::PROOF => n >= 41 && n <= 105 && usize::from(r.body[32]) + 33 == n,
                wire::STATUS => n == 32,
                wire::OUTPUT => n == 49,
                wire::DETACH => n == 40,
                wire::STOP => {
                    crate::audio_source_stop::FastSourceStop::parse(&r.body).is_ok_and(|v| {
                        self.approved.as_ref().is_some_and(|a| {
                            v.source == a.binding.source
                                && v.generation == a.binding.source_generation
                                && v.digest == a.binding.source_digest
                        })
                    })
                }
                wire::SIGNED_SOURCE_STOP => {
                    crate::audio_source_stop::SignedSourceStop::parse(&r.body).is_ok_and(|v| {
                        self.observation
                            .as_ref()
                            .is_some_and(|o| v.proof.matches_observation(o))
                    })
                }
                wire::SOURCE_STOP_STATUS => {
                    crate::audio_source_stop::StopStatusQuery::parse(&r.body).is_ok()
                }
                wire::APPLIED_RESULT => {
                    crate::audio_source_stop::AppliedResultQuery::parse(&r.body).is_ok()
                }
                _ => false,
            };
            if !valid
                || self.approved.is_none()
                    && matches!(r.kind, wire::OUTPUT | wire::DETACH | wire::STOP)
            {
                return Err(denied());
            }
            self.last_request = r.request;
            self.pending.insert(r.request, r.kind);
        } else if r.kind == wire::NOTICE {
            if self.observation.is_some() {
                return Err(denied());
            }
            let observed = Observation::parse(&r.body)?;
            let facts = self.facts.as_ref().ok_or_else(denied)?;
            if observed.native != facts.peer_hash || observed.phone != facts.local_hash {
                return Err(denied());
            }
            self.observation = Some(observed);
        } else {
            self.complete_pending(r)?;
        }
        Ok(())
    }
    fn complete_pending(&mut self, r: &Record) -> io::Result<()> {
        let kind = self.pending.get(&r.request).ok_or_else(wire::invalid)?;
        if r.kind != wire::ERROR && r.kind != kind + 128 {
            return Err(wire::invalid());
        }
        self.pending.remove(&r.request);
        Ok(())
    }
    fn pending_capacity(&self, kind: u8) -> bool {
        if wire::negative(kind) {
            !self.pending.values().any(|k| wire::negative(*k))
        } else {
            self.pending
                .values()
                .filter(|k| !wire::negative(**k))
                .count()
                < MAX_PENDING
        }
    }
    fn write_capacity(&self, is_negative: bool) -> bool {
        let negative = self.writes.iter().filter(|v| v.negative).count();
        if is_negative {
            negative == 0
        } else {
            self.writes.len() - negative < MAX_PENDING
        }
    }
    pub fn queue_control(&mut self, record: Record, now: u64) -> io::Result<()> {
        self.advance(now)?;
        let negative = if self.role == Role::Native {
            self.pending
                .get(&record.request)
                .is_some_and(|k| wire::negative(*k))
        } else {
            wire::negative(record.kind)
        };
        if self.facts.is_none() || !self.write_capacity(negative) {
            return Err(io::Error::from(io::ErrorKind::WouldBlock));
        }
        let bytes = record.encode()?;
        if self.role == Role::Phone {
            if !(wire::INFO..=wire::APPLIED_RESULT).contains(&record.kind)
                || self.last_request == u64::MAX
                || record.request != self.last_request + 1
                || !self.pending_capacity(record.kind)
            {
                return Err(wire::invalid());
            }
            if self.approved.is_none()
                && matches!(record.kind, wire::OUTPUT | wire::DETACH | wire::STOP)
            {
                return Err(denied());
            }
            if record.kind == wire::SIGNED_SOURCE_STOP {
                let stop = crate::audio_source_stop::SignedSourceStop::parse(&record.body)?;
                if !self
                    .observation
                    .as_ref()
                    .is_some_and(|o| stop.proof.matches_observation(o))
                {
                    return Err(denied());
                }
            }
            self.last_request = record.request;
            self.pending.insert(record.request, record.kind);
        } else {
            self.complete_pending(&record)?;
        }
        self.writes.push_back(QueuedControl {
            bytes,
            offset: 0,
            negative,
        });
        Ok(())
    }
    pub fn service_egress(
        &mut self,
        socket: &UdpSocket,
        writable: bool,
        now: u64,
    ) -> io::Result<()> {
        self.advance(now)?;
        self.flush_stream()?;
        self.egress
            .service_with_hook_at(socket, self.local, &mut self.conn, writable, |_| Ok(()))
            .map_err(|_| denied())
    }
    fn flush_stream(&mut self) -> io::Result<()> {
        for _ in 0..=MAX_PENDING {
            let Some(QueuedControl {
                bytes, offset: at, ..
            }) = self.writes.front_mut()
            else {
                break;
            };
            match self.conn.stream_send(0, &bytes[*at..], false) {
                Ok(n) => {
                    *at += n;
                    if *at == bytes.len() {
                        self.writes.pop_front();
                    } else {
                        break;
                    }
                }
                Err(quiche::Error::Done) => break,
                // Stream 0 is client-initiated. The public INFO request opens it;
                // a server cannot publish its observation before those bytes arrive.
                Err(quiche::Error::InvalidStreamState(_))
                    if self.role == Role::Native && self.last_request == 0 =>
                {
                    break
                }
                Err(_) => return Err(denied()),
            }
        }
        Ok(())
    }
    /// The caller is the bound SYSTEM control owner. Network records cannot call
    /// this operation. Source/output readiness remain separate N4 observations.
    pub fn install_native(
        &mut self,
        scope: &SourceScope,
        expected_boot: [u8; 32],
        binding: DeliveryBinding,
        grant: &ControlGrant,
        now: u64,
    ) -> io::Result<()> {
        let result = self.install_native_inner(scope, expected_boot, binding, grant, now);
        if result.is_err() {
            self.retire();
        }
        result
    }
    fn install_native_inner(
        &mut self,
        scope: &SourceScope,
        expected_boot: [u8; 32],
        binding: DeliveryBinding,
        grant: &ControlGrant,
        now: u64,
    ) -> io::Result<()> {
        self.advance(now)?;
        if self.role != Role::Native
            || expected_boot == [0; 32]
            || grant.revision == 0
            || grant.closed
            || grant.boot != expected_boot
            || now >= grant.until
            || !binding.matches(scope, self.observation.as_ref().ok_or_else(denied)?, grant)
        {
            return Err(denied());
        }
        if let Some(old) = &self.approved {
            if old.binding != binding
                || old.boot != grant.boot
                || grant.revision < old.revision
                || grant.revision == old.revision && grant.until != old.until
            {
                return Err(denied());
            }
        }
        self.deadline = grant.until;
        self.approved = Some(Approved {
            binding,
            boot: grant.boot,
            revision: grant.revision,
            until: grant.until,
        });
        Ok(())
    }
    pub fn retire(&mut self) {
        self.retired = true;
        self.approved = None;
        self.writes.clear();
        self.pending.clear();
        self.read_backlog.clear();
        self.egress = Egress::default();
        let _ = self.conn.close(true, 0, b"");
    }
    pub fn is_approved(&self) -> bool {
        self.approved.is_some() && !self.retired
    }
    pub fn deadline(&self) -> u64 {
        self.deadline
    }
    pub fn service_timeout(&mut self, now: u64) -> io::Result<()> {
        self.advance(now)?;
        if self.conn.timeout().is_some_and(|t| t.is_zero()) {
            self.conn.on_timeout();
        }
        self.advance(now)
    }
    pub fn network_deadline(&self) -> Option<std::time::Instant> {
        crate::reactor::next_deadline(self.conn.timeout_instant(), self.egress.deadline(), None)
    }
    pub fn needs_write(&self) -> bool {
        self.egress.needs_write_readiness()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> (Vec<u8>, Vec<u8>, ControlGrant) {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-transport-v1.json"
        ))
        .unwrap();
        let bytes = |key: &str| {
            let s = f[key].as_str().unwrap();
            (0..s.len())
                .step_by(2)
                .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
                .collect::<Vec<_>>()
        };
        (
            bytes("sourceHex"),
            bytes("deliveryHex"),
            ControlGrant::parse(&bytes("controlHex")).unwrap(),
        )
    }
    #[test]
    fn canonical_binding_rejects_context_substitution_and_trailing_or_truncated_records() {
        let (source, delivery, g) = fixture();
        let scope = SourceScope::parse(&source).unwrap();
        let binding = DeliveryBinding::parse(&delivery).unwrap();
        let observation = Observation {
            connection: [1; 16],
            native: [15; 32],
            phone: [16; 32],
            nonce: [17; 32],
        };
        assert!(binding.matches(&scope, &observation, &g));
        assert_eq!(scope.grace100ns, 2000000);
        for which in 0..13 {
            let mut bad = g.clone();
            let mut observed = observation.clone();
            let mut b = binding.clone();
            match which {
                0 => bad.host[0] ^= 1,
                1 => bad.namespace[0] ^= 1,
                2 => bad.enrollment[0] ^= 1,
                3 => bad.worker[0] ^= 1,
                4 => bad.source[0] ^= 1,
                5 => bad.source_generation += 1,
                6 => bad.source_digest[0] ^= 1,
                7 => bad.binding[0] ^= 1,
                8 => bad.delivery_generation += 1,
                9 => bad.delivery_digest[0] ^= 1,
                10 => observed.native[0] ^= 1,
                11 => observed.phone[0] ^= 1,
                _ => b.nonce[0] ^= 1,
            }
            assert!(!b.matches(&scope, &observed, &bad), "substitution {which}");
        }
        for end in 0..source.len() {
            assert!(SourceScope::parse(&source[..end]).is_err());
        }
        for end in 0..delivery.len() {
            assert!(DeliveryBinding::parse(&delivery[..end]).is_err());
        }
        let mut bad = source;
        bad.push(0);
        assert!(SourceScope::parse(&bad).is_err());
        let mut bad = delivery;
        bad.push(0);
        assert!(DeliveryBinding::parse(&bad).is_err());
    }
    fn pump(from: &mut ControlConnection, to: &mut ControlConnection) {
        from.advance(100).unwrap();
        from.flush_stream().unwrap();
        let mut buf = [0u8; 1350];
        for _ in 0..32 {
            match from.conn.send(&mut buf) {
                Ok((n, i)) => {
                    to.receive(
                        &mut buf[..n],
                        quiche::RecvInfo {
                            from: i.from,
                            to: i.to,
                        },
                        100,
                    )
                    .unwrap();
                }
                Err(quiche::Error::Done) => break,
                Err(e) => panic!("{e:?}"),
            }
        }
    }
    #[test]
    fn actual_tls_control_is_quarantined_bounded_and_fixed_route() {
        let a = "127.0.0.1:44001".parse().unwrap();
        let b = "127.0.0.1:44002".parse().unwrap();
        let mut phone = DeliveryTls::generate(Role::Phone)
            .unwrap()
            .connect(a, b, 10000, 10000)
            .unwrap();
        let mut packet = [0u8; 1350];
        let (n, i) = phone.conn.send(&mut packet).unwrap();
        let mut native = DeliveryTls::generate(Role::Native)
            .unwrap()
            .accept(
                &mut packet[..n],
                quiche::RecvInfo {
                    from: i.from,
                    to: i.to,
                },
                10000,
                10000,
            )
            .unwrap();
        for _ in 0..64 {
            pump(&mut native, &mut phone);
            pump(&mut phone, &mut native);
            if native.facts(100).unwrap().is_some() && phone.facts(100).unwrap().is_some() {
                break;
            }
        }
        assert!(!native.is_approved() && !phone.is_approved());
        assert!(native.read_control(100).unwrap().is_empty());
        assert!(phone.read_control(100).unwrap().is_empty());
        assert!(phone
            .queue_control(
                Record {
                    kind: wire::STOP,
                    request: 1,
                    body: vec![0; 40]
                },
                100
            )
            .is_err());
        phone
            .queue_control(
                Record {
                    kind: wire::INFO,
                    request: 1,
                    body: vec![],
                },
                100,
            )
            .unwrap();
        pump(&mut phone, &mut native);
        let requests = native.read_control(100).unwrap();
        assert_eq!(requests.len(), 1);
        assert_eq!(requests[0].request, 1);
        assert!(native
            .queue_control(
                Record {
                    kind: 130,
                    request: 1,
                    body: vec![]
                },
                100
            )
            .is_err());
        assert_eq!(native.pending.len(), 1);
        native
            .queue_control(
                Record {
                    kind: 129,
                    request: 1,
                    body: b"public metadata".to_vec(),
                },
                100,
            )
            .unwrap();
        pump(&mut native, &mut phone);
        let replies = phone.read_control(100).unwrap();
        assert_eq!(replies.len(), 2);
        assert_eq!(replies[0].kind, wire::NOTICE);
        assert_eq!(replies[1].body, b"public metadata");
        assert_eq!(
            native.observation(100).unwrap(),
            phone.observation(100).unwrap()
        );
        for request in 2..=9 {
            phone
                .queue_control(
                    Record {
                        kind: wire::INFO,
                        request,
                        body: vec![],
                    },
                    100,
                )
                .unwrap();
        }
        assert_eq!(phone.pending.len(), MAX_PENDING);
        assert!(phone
            .queue_control(
                Record {
                    kind: wire::INFO,
                    request: 10,
                    body: vec![]
                },
                100
            )
            .is_err());
        let vector: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-source-stop-v1.json"
        ))
        .unwrap();
        let hex = vector["signedStopHex"].as_str().unwrap();
        let mut stop = (0..hex.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
            .collect::<Vec<_>>();
        let observed = native.observation(100).unwrap().unwrap();
        for (old, new) in [
            (vec![19; 16], observed.connection.to_vec()),
            (vec![15; 32], observed.native.to_vec()),
            (vec![16; 32], observed.phone.to_vec()),
            (vec![17; 32], observed.nonce.to_vec()),
        ] {
            let at = stop.windows(old.len()).position(|v| v == old).unwrap();
            stop[at..at + old.len()].copy_from_slice(&new);
        }
        phone
            .queue_control(
                Record {
                    kind: wire::SIGNED_SOURCE_STOP,
                    request: 10,
                    body: stop,
                },
                100,
            )
            .unwrap();
        assert_eq!(phone.pending.len(), MAX_PENDING + 1);
        assert_eq!(phone.writes.len(), MAX_PENDING + 1);
        pump(&mut phone, &mut native);
        let mut requests = native.read_control(100).unwrap();
        requests.extend(native.read_control(100).unwrap());
        assert_eq!(requests.len(), MAX_PENDING + 1);
        assert_eq!(requests.last().unwrap().kind, wire::SIGNED_SOURCE_STOP);
        for request in 2..=9 {
            native
                .queue_control(
                    Record {
                        kind: 129,
                        request,
                        body: vec![],
                    },
                    100,
                )
                .unwrap();
        }
        native
            .queue_control(
                Record {
                    kind: wire::ERROR,
                    request: 10,
                    body: vec![],
                },
                100,
            )
            .unwrap();
        assert_eq!(native.writes.len(), MAX_PENDING + 1);
        pump(&mut native, &mut phone);
        let mut replies = phone.read_control(100).unwrap();
        replies.extend(phone.read_control(100).unwrap());
        assert_eq!(replies.len(), MAX_PENDING + 1);
        assert!(phone.pending.is_empty());
        assert!(!native.is_approved() && !phone.is_approved()); // Envelope parsing confers no authority.
        assert!(!native
            .receive(
                &mut [0u8; 1],
                quiche::RecvInfo {
                    from: "127.0.0.1:45000".parse().unwrap(),
                    to: b
                },
                100
            )
            .unwrap());
        let (source, mut bytes, mut grant) = fixture();
        let scope = SourceScope::parse(&source).unwrap();
        let observed = native.observation(100).unwrap().unwrap();
        for (old, new) in [
            (15, observed.native),
            (16, observed.phone),
            (17, observed.nonce),
        ] {
            let at = bytes.windows(32).position(|b| b == [old; 32]).unwrap();
            bytes[at..at + 32].copy_from_slice(&new);
        }
        let binding = DeliveryBinding::parse(&bytes).unwrap();
        grant.delivery_digest = binding.digest;
        native
            .install_native(&scope, grant.boot, binding.clone(), &grant, 100)
            .unwrap();
        assert!(native.is_approved());
        // An exact projection retry keeps the cutoff; a stale revision closes
        // admission and cannot be revived by replaying the prior valid grant.
        native
            .install_native(&scope, grant.boot, binding.clone(), &grant, 101)
            .unwrap();
        assert_eq!(native.deadline(), 20000);
        let mut bad = grant.clone();
        bad.revision -= 1;
        assert!(native
            .install_native(&scope, grant.boot, binding.clone(), &bad, 102)
            .is_err());
        assert!(!native.is_approved());
        assert!(native
            .install_native(&scope, grant.boot, binding, &grant, 103)
            .is_err());
        assert!(phone.facts(10000).is_err());
        assert!(!phone.is_approved());
        assert!(phone.pending.is_empty() && phone.writes.is_empty());
    }
}
