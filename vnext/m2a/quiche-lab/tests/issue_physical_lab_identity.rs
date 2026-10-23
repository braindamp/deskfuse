//! Run explicitly, on the Windows host, to create disposable local-only creds.
mod common;

#[test]
#[ignore = "creates private test credentials on demand, never in the repository"]
fn issue_local_physical_lab_identity() -> common::LabResult<()> {
    let path = std::env::var("DESKFUSE_LAB_CERT_DIR")
        .map_err(|_| "set DESKFUSE_LAB_CERT_DIR to an absolute directory OUTSIDE the git worktree")?;
    let dest = std::path::PathBuf::from(path);
    if !dest.is_absolute() {
        return Err("DESKFUSE_LAB_CERT_DIR must be an absolute directory".into());
    }
    // Don't produce credentials inside this repository, even though gitignore
    // protects many credential extensions.
    let repo = std::fs::canonicalize(env!("CARGO_MANIFEST_DIR"))?;
    let dst_parent = dest
        .parent()
        .ok_or("destination has no parent")?;
    let canonical_parent = std::fs::canonicalize(dst_parent)?;
    if canonical_parent.starts_with(repo) {
        return Err("REFUSED: choose a private credential directory outside the public Git checkout".into());
    }
    common::issue_test_identities(&dest)?;
    println!("PASS: disposable localhost-lab identities created in {}", dest.display());
    println!("Private keys must never be pushed to GitHub or shared in chat.");
    Ok(())
}
