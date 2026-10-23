//! Locally generated, disposable TLS identities for test processes only.
//! Do not ship these credentials or upload their private keys to GitHub.
use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, DnType,
    ExtendedKeyUsagePurpose, IsCa, KeyPair, KeyUsagePurpose,
};
use std::error::Error;
use std::fs;
use std::path::Path;

pub type LabResult<T> = Result<T, Box<dyn Error>>;

pub fn issue_test_identities(dir: &Path) -> LabResult<()> {
    // Require a fresh, empty directory to prevent accidentally replacing an
    // established keypair. The calling test owns the destination directory.
    fs::create_dir_all(dir)?;
    if fs::read_dir(dir)?.next().is_some() {
        return Err(format!("REFUSED: certificate directory must be empty: {}", dir.display()).into());
    }

    let mut ca_params = CertificateParams::new(Vec::<String>::new())?;
    ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    ca_params.distinguished_name.push(DnType::CommonName, "DeskFuse disposable physical lab CA");
    ca_params.key_usages = vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
    let ca = CertifiedIssuer::self_signed(ca_params, KeyPair::generate()?)?;

    let mut server_params = CertificateParams::new(vec!["deskfuse-lab.local".to_owned()])?;
    server_params.distinguished_name.push(DnType::CommonName, "deskfuse-lab.local");
    server_params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    server_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    let server_key = KeyPair::generate()?;
    let server_cert = server_params.signed_by(&server_key, &ca)?;

    let mut client_params = CertificateParams::new(Vec::<String>::new())?;
    client_params.distinguished_name.push(DnType::CommonName, "deskfuse-android-lab");
    client_params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    client_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
    let client_key = KeyPair::generate()?;
    let client_cert = client_params.signed_by(&client_key, &ca)?;

    // Trust anchor and leaf chains; keys exist only on the user's machine
    // (or the ephemeral CI runner) and are not built into any executable.
    fs::write(dir.join("ca.pem"), ca.pem())?;
    fs::write(
        dir.join("server.pem"),
        format!("{}\n{}", server_cert.pem(), ca.pem()),
    )?;
    fs::write(dir.join("server.key"), server_key.serialize_pem())?;
    fs::write(
        dir.join("client.pem"),
        format!("{}\n{}", client_cert.pem(), ca.pem()),
    )?;
    fs::write(dir.join("client.key"), client_key.serialize_pem())?;
    Ok(())
}
