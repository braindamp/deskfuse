//! Delivery-local in-memory TLS. A verified candidate is QUARANTINED, never an
//! enrolled peer. Only broker/device proof may authorize its exact connection.
use boring::{
    asn1::Asn1Time,
    bn::{BigNum, MsbOption},
    ec::{EcGroup, EcKey},
    hash::MessageDigest,
    nid::Nid,
    pkey::{PKey, Private},
    ssl::{SslAlert, SslContextBuilder, SslMethod, SslOptions, SslVerifyError, SslVerifyMode},
    x509::{
        extension::{BasicConstraints, ExtendedKeyUsage, KeyUsage},
        X509Name, X509Ref, X509,
    },
};
use foreign_types::ForeignTypeRef;
use std::{
    io,
    time::{SystemTime, UNIX_EPOCH},
};

pub const ALPN: &[u8] = b"df-audio-authority-2";
pub const MAX_CERT: usize = 2048;
pub const MAX_CONTROL: usize = 8192;
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Role {
    Native,
    Phone,
}
impl Role {
    fn peer(self) -> Self {
        match self {
            Self::Native => Self::Phone,
            Self::Phone => Self::Native,
        }
    }
}
fn invalid() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "Audio TLS candidate refused",
    )
}
fn crypto<E>(_: E) -> io::Error {
    invalid()
}

/// Private key has no serializer/export accessor. This value is consumed into
/// exactly one connection configuration; the TLS context retains its key in RAM.
pub(crate) struct Identity {
    key: PKey<Private>,
    cert: X509,
    pub(crate) der: Vec<u8>,
    pub(crate) hash: [u8; 32],
}
impl Identity {
    pub(crate) fn generate(role: Role) -> io::Result<Self> {
        let group = EcGroup::from_curve_name(Nid::X9_62_PRIME256V1).map_err(crypto)?;
        let key = PKey::from_ec_key(EcKey::generate(&group).map_err(crypto)?).map_err(crypto)?;
        let mut name = X509Name::builder().map_err(crypto)?;
        name.append_entry_by_nid(
            Nid::COMMONNAME,
            match role {
                Role::Native => "DeskFuse native delivery",
                Role::Phone => "DeskFuse phone delivery",
            },
        )
        .map_err(crypto)?;
        let name = name.build();
        let mut b = X509::builder().map_err(crypto)?;
        b.set_version(2).map_err(crypto)?;
        b.set_subject_name(&name).map_err(crypto)?;
        b.set_issuer_name(&name).map_err(crypto)?;
        let mut serial = BigNum::new().map_err(crypto)?;
        serial.rand(128, MsbOption::ONE, false).map_err(crypto)?;
        let serial = serial.to_asn1_integer().map_err(crypto)?;
        b.set_serial_number(&serial).map_err(crypto)?;
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(crypto)?
            .as_secs();
        let start = now.saturating_sub(60);
        let end = start.checked_add(1800).ok_or_else(invalid)?;
        let before = Asn1Time::from_unix(start.try_into().map_err(crypto)?).map_err(crypto)?;
        b.set_not_before(&before).map_err(crypto)?;
        let after = Asn1Time::from_unix(end.try_into().map_err(crypto)?).map_err(crypto)?;
        b.set_not_after(&after).map_err(crypto)?;
        b.set_pubkey(&key).map_err(crypto)?;
        let basic = BasicConstraints::new().critical().build().map_err(crypto)?;
        b.append_extension(&basic).map_err(crypto)?;
        let ku = KeyUsage::new()
            .critical()
            .digital_signature()
            .build()
            .map_err(crypto)?;
        b.append_extension(&ku).map_err(crypto)?;
        let mut usage = ExtendedKeyUsage::new();
        match role {
            Role::Native => {
                usage.server_auth();
            }
            Role::Phone => {
                usage.client_auth();
            }
        }
        let usage = usage.build().map_err(crypto)?;
        b.append_extension(&usage).map_err(crypto)?;
        b.sign(&key, MessageDigest::sha256()).map_err(crypto)?;
        let cert = b.build();
        let der = cert.to_der().map_err(crypto)?;
        validate(&cert, &der, role)?;
        let hash = boring::sha::sha256(&der);
        Ok(Self {
            key,
            cert,
            der,
            hash,
        })
    }
    pub(crate) fn config(self, role: Role) -> io::Result<(quiche::Config, Vec<u8>, [u8; 32])> {
        let mut ctx = SslContextBuilder::new(SslMethod::tls()).map_err(crypto)?;
        ctx.set_certificate(&self.cert).map_err(crypto)?;
        ctx.set_private_key(&self.key).map_err(crypto)?;
        ctx.check_private_key().map_err(crypto)?;
        ctx.set_options(SslOptions::NO_TICKET);
        let mode = SslVerifyMode::PEER
            | if role == Role::Native {
                SslVerifyMode::FAIL_IF_NO_PEER_CERT
            } else {
                SslVerifyMode::empty()
            };
        ctx.set_custom_verify_callback(mode, move |ssl| {
            let fail = || SslVerifyError::Invalid(SslAlert::BAD_CERTIFICATE);
            let cert = ssl.peer_certificate().ok_or_else(fail)?;
            let der = cert.to_der().map_err(|_| fail())?;
            // Boring's server-side chain excludes the separately returned leaf;
            // client-side includes it. Normalize to exactly ONE wire leaf.
            let count = ssl.peer_cert_chain().map_or(0, |c| c.len());
            if count != if role == Role::Native { 0 } else { 1 } {
                return Err(fail());
            }
            validate(&cert, &der, role.peer()).map_err(|_| fail())
        });
        let mut config = quiche::Config::with_boring_ssl_ctx_builder(quiche::PROTOCOL_VERSION, ctx)
            .map_err(crypto)?;
        config.set_application_protos(&[ALPN]).map_err(crypto)?;
        config.set_max_recv_udp_payload_size(1350);
        config.set_max_send_udp_payload_size(1350);
        config.set_initial_max_data(65536);
        config.set_initial_max_stream_data_bidi_local(MAX_CONTROL as u64 * 2);
        config.set_initial_max_stream_data_bidi_remote(MAX_CONTROL as u64 * 2);
        config.set_initial_max_streams_bidi(1);
        config.set_initial_max_streams_uni(0);
        config.set_disable_active_migration(true);
        config.enable_dgram(true, 8, 8);
        // No verify_peer(false), session import or enable_early_data. Do not
        // overwrite this context's custom mutual-certificate verification.
        Ok((config, self.der, self.hash))
    }
}

/// Certificate lifetime bounds the candidate PROFILE, not audio authority.
/// It is not a wall-clock lease or reason to restart a healthy renewed delivery.
pub(crate) fn validate(cert: &X509Ref, der: &[u8], role: Role) -> io::Result<()> {
    if der.is_empty()
        || der.len() > MAX_CERT
        || cert.to_der().map_err(crypto)? != der
        || cert.subject_name().to_der().map_err(crypto)?
            != cert.issuer_name().to_der().map_err(crypto)?
        || cert.signature_algorithm().object().nid() != Nid::ECDSA_WITH_SHA256
    {
        return Err(invalid());
    }
    let key = cert.public_key().map_err(crypto)?;
    let spki = key.public_key_to_der().map_err(crypto)?;
    const P256: &[u8] = &[
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08,
        0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    ];
    if spki.len() != 91 || !spki.starts_with(P256) || !cert.verify(&key).map_err(crypto)? {
        return Err(invalid());
    }
    let span = cert.not_before().diff(cert.not_after()).map_err(crypto)?;
    if span.days != 0 || !(1..=1800).contains(&span.secs) {
        return Err(invalid());
    }
    // These read-only library APIs validate the native X.509 extensions. No
    // ad-hoc ASN.1 parser or textual certificate interpretation is introduced.
    let (flags, usage, extended, ca) = unsafe {
        (
            boring_sys::X509_get_extension_flags(cert.as_ptr()),
            boring_sys::X509_get_key_usage(cert.as_ptr()),
            boring_sys::X509_get_extended_key_usage(cert.as_ptr()),
            boring_sys::X509_check_ca(cert.as_ptr()),
        )
    };
    let expected = if role == Role::Native {
        boring_sys::XKU_SSL_SERVER
    } else {
        boring_sys::XKU_SSL_CLIENT
    };
    if flags
        & (boring_sys::EXFLAG_BCONS | boring_sys::EXFLAG_KUSAGE | boring_sys::EXFLAG_XKUSAGE) as u32
        != 7
        || flags
            & (boring_sys::EXFLAG_CA
                | boring_sys::EXFLAG_INVALID
                | boring_sys::EXFLAG_CRITICAL
                | boring_sys::EXFLAG_V1) as u32
            != 0
        || ca != 0
        || usage != boring_sys::X509v3_KU_DIGITAL_SIGNATURE as u32
        || extended != expected as u32
    {
        return Err(invalid());
    }
    Ok(())
}

pub(crate) fn require_established(conn: &quiche::Connection, role: Role) -> io::Result<[u8; 32]> {
    if !conn.is_established()
        || conn.is_in_early_data()
        || conn.is_resumed()
        || conn.application_proto() != ALPN
    {
        return Err(invalid());
    }
    let der = conn.peer_cert().ok_or_else(invalid)?;
    if der.len() > MAX_CERT {
        return Err(invalid());
    }
    let cert = X509::from_der(der).map_err(crypto)?;
    validate(&cert, der, role.peer())?;
    Ok(boring::sha::sha256(der))
}

#[cfg(test)]
mod tests {
    use super::*;
    fn exchange(mut cc: quiche::Config, mut sc: quiche::Config) -> bool {
        let a = "127.0.0.1:43101".parse().unwrap();
        let b = "127.0.0.1:43102".parse().unwrap();
        let id = quiche::ConnectionId::from_ref(&[0x46; 16]);
        let mut c = quiche::connect(None, &id, a, b, &mut cc).unwrap();
        let mut buf = [0u8; 1350];
        let (n, i) = c.send(&mut buf).unwrap();
        let h = quiche::Header::from_slice(&mut buf[..n], quiche::MAX_CONN_ID_LEN).unwrap();
        let mut s = quiche::accept(&h.dcid, None, b, a, &mut sc).unwrap();
        if s.recv(
            &mut buf[..n],
            quiche::RecvInfo {
                from: i.from,
                to: i.to,
            },
        )
        .is_err()
        {
            return false;
        }
        fn turn(
            from: &mut quiche::Connection,
            to: &mut quiche::Connection,
            buf: &mut [u8],
        ) -> bool {
            for _ in 0..32 {
                match from.send(buf) {
                    Ok((n, i)) => {
                        if to
                            .recv(
                                &mut buf[..n],
                                quiche::RecvInfo {
                                    from: i.from,
                                    to: i.to,
                                },
                            )
                            .is_err()
                        {
                            return false;
                        }
                    }
                    Err(quiche::Error::Done) => break,
                    Err(_) => return false,
                }
            }
            true
        }
        for _ in 0..64 {
            if !turn(&mut s, &mut c, &mut buf) || !turn(&mut c, &mut s, &mut buf) {
                return false;
            }
            if s.is_established() && c.is_established() {
                return require_established(&s, Role::Native).is_ok()
                    && require_established(&c, Role::Phone).is_ok();
            }
        }
        false
    }
    #[test]
    fn mutual_tls_rejects_wrong_role_missing_client_identity_and_legacy_alpn() {
        for case in 0..3 {
            let mut cc = if case == 1 {
                let mut context = SslContextBuilder::new(SslMethod::tls()).unwrap();
                // Deliberately anonymous test client, never a production config.
                context.set_custom_verify_callback(SslVerifyMode::PEER, |_| Ok(()));
                let mut c =
                    quiche::Config::with_boring_ssl_ctx_builder(quiche::PROTOCOL_VERSION, context)
                        .unwrap();
                c.set_application_protos(&[ALPN]).unwrap();
                c
            } else {
                Identity::generate(if case == 0 { Role::Native } else { Role::Phone })
                    .unwrap()
                    .config(Role::Phone)
                    .unwrap()
                    .0
            };
            if case == 2 {
                cc.set_application_protos(&[b"legacy-audio"]).unwrap();
            }
            let sc = Identity::generate(Role::Native)
                .unwrap()
                .config(Role::Native)
                .unwrap()
                .0;
            assert!(
                !exchange(cc, sc),
                "invalid TLS case {case} established authority-profile TLS"
            );
        }
    }
    #[test]
    fn ephemeral_certificates_are_distinct_role_checked_bounded_and_self_signed() {
        let a = Identity::generate(Role::Native).unwrap();
        let b = Identity::generate(Role::Native).unwrap();
        assert_ne!(a.hash, b.hash);
        assert!(a.der.len() < MAX_CERT);
        assert!(validate(&a.cert, &a.der, Role::Phone).is_err());
        let mut trailing = a.der.clone();
        trailing.push(0);
        assert!(validate(&a.cert, &trailing, Role::Native).is_err());
        let p = Identity::generate(Role::Phone).unwrap();
        assert!(validate(&p.cert, &p.der, Role::Phone).is_ok());
    }
    #[test]
    fn actual_mutual_quiche_tls_has_only_quarantined_fresh_in_memory_identities() {
        let (mut cc, client_der, client_hash) = Identity::generate(Role::Phone)
            .unwrap()
            .config(Role::Phone)
            .unwrap();
        let (mut sc, server_der, server_hash) = Identity::generate(Role::Native)
            .unwrap()
            .config(Role::Native)
            .unwrap();
        let a = "127.0.0.1:43001".parse().unwrap();
        let b = "127.0.0.1:43002".parse().unwrap();
        let id = quiche::ConnectionId::from_ref(&[0x45; 16]);
        let mut c = quiche::connect(None, &id, a, b, &mut cc).unwrap();
        let mut buf = [0u8; 1350];
        let (n, info) = c.send(&mut buf).unwrap();
        let header = quiche::Header::from_slice(&mut buf[..n], quiche::MAX_CONN_ID_LEN).unwrap();
        let mut s = quiche::accept(&header.dcid, None, b, a, &mut sc).unwrap();
        s.recv(
            &mut buf[..n],
            quiche::RecvInfo {
                from: info.from,
                to: info.to,
            },
        )
        .unwrap();
        fn pump(from: &mut quiche::Connection, to: &mut quiche::Connection, buf: &mut [u8]) {
            for _ in 0..32 {
                match from.send(buf) {
                    Ok((n, info)) => {
                        to.recv(
                            &mut buf[..n],
                            quiche::RecvInfo {
                                from: info.from,
                                to: info.to,
                            },
                        )
                        .unwrap();
                    }
                    Err(quiche::Error::Done) => break,
                    Err(e) => panic!("TLS exchange {e:?}"),
                }
            }
        }
        for _ in 0..64 {
            pump(&mut s, &mut c, &mut buf);
            pump(&mut c, &mut s, &mut buf);
            if s.is_established() && c.is_established() {
                break;
            }
        }
        assert_eq!(require_established(&s, Role::Native).unwrap(), client_hash);
        assert_eq!(require_established(&c, Role::Phone).unwrap(), server_hash);
        assert_eq!(s.peer_cert().unwrap(), client_der);
        assert_eq!(c.peer_cert().unwrap(), server_der);
    }
}
