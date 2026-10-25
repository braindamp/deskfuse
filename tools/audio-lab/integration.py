"""DeskFuse Audio Lab v0.6: read-only Windows source/receipt integration.

No endpoint creation, default-device changes, driver operations, key handling,
or authenticated enrollment decisions happen here. Native runner is final authority.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

ROLES = ("windows_rust", "android_rust", "android_native", "windows_capture")
POLICIES = ("reference", "minimum")
SOURCES = ("mm1", "deskfuse")
BROKER_PIPE = r"\\.\pipe\DeskFuse.AudioBroker.v1"
COMMIT_RE = re.compile(r"^[0-9a-fA-F]{40}$")
SHA_RE = re.compile(r"^[0-9a-fA-F]{64}$")
REQUIRED_CAPS = {
    "windows_endpoint_lease": True,
    "android_audio_abi": 8,
}


def broker_preflight() -> dict:
    """One read-only pipe connection attempt; never send create/list/release.

    A successful connection establishes pipe reachability only, not driver or
    endpoint readiness, user authorization, or ownership.
    """
    if os.name != "nt":
        return {"state": "not_checked", "detail": "Windows broker probe requires Windows"}
    try:
        with open(BROKER_PIPE, "r+b", buffering=0):
            pass
    except (OSError, PermissionError) as exc:
        return {"state": "unavailable", "detail": f"Broker named pipe unavailable ({type(exc).__name__}); no remediation attempted"}
    return {"state": "reachable", "detail": "Broker pipe reachable; installed driver/endpoint NOT verified"}


def receipt_path(cfg: dict) -> Path:
    value = cfg.get("build_receipt")
    return Path(value).expanduser() if isinstance(value, str) and value else Path(cfg.get("repo_root") or "__not_configured__") / "vnext/m1/build/audio-pair-build.json"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def receipt_preflight(cfg: dict, roles: dict[str, Path], *, checkout_head: str | None = None) -> dict:
    """Independently inspect a versioned receipt; do not bless artifacts for playback.

    Paths in an older receipt are informational: the native runner matches by
    artifact ROLE and sha256 rather than stale absolute paths. Verify hashes
    once before launch; the runner MUST independently check them again.
    """
    result = {"state": "blocked", "problems": [], "artifacts": {},
              "capabilities": {}, "source_commit": None, "receipt": str(receipt_path(cfg))}
    problems = result["problems"]
    try:
        raw = receipt_path(cfg).read_text(encoding="utf-8-sig")
        receipt = json.loads(raw)
        if not isinstance(receipt, dict) or receipt.get("schema") != 1:
            raise ValueError("Unsupported receipt schema")
        elements = receipt.get("artifacts")
        if not isinstance(elements, list):
            raise ValueError("Receipt artifacts must be a list")
        caps = receipt.get("capabilities")
        if not isinstance(caps, dict):
            raise ValueError("Receipt capabilities missing")
        result["capabilities"] = {k: caps.get(k) for k in (
            "windows_endpoint_lease", "windows_engine_period_policies", "windows_capture_policies", "android_audio_abi")}
        commit = receipt.get("source_commit")
        if not isinstance(commit, str) or not COMMIT_RE.fullmatch(commit):
            problems.append("Receipt has no valid 40-character source commit")
        else:
            result["source_commit"] = commit
            if checkout_head and commit.lower() != checkout_head.lower():
                problems.append("Receipt source commit differs from checkout HEAD (archived sets require independent runner selection)")
        for cap, needed in REQUIRED_CAPS.items():
            if caps.get(cap) != needed:
                problems.append(f"Receipt capability {cap} does not match required {needed!r}")
        allowed_periods = caps.get("windows_engine_period_policies")
        allowed_capture = caps.get("windows_capture_policies")
        if not isinstance(allowed_periods, list) or "reference" not in allowed_periods:
            problems.append("Missing reference Windows engine period capability")
        if not isinstance(allowed_capture, list) or "reference" not in allowed_capture:
            problems.append("Missing reference Windows capture policy capability")
        seen: set[str] = set()
        by_role = {}
        for row in elements:
            if not isinstance(row, dict):
                continue
            role = row.get("role")
            if role not in ROLES:
                continue
            if role in seen:
                problems.append(f"Ambiguous duplicate receipt role: {role}")
            seen.add(role)
            by_role[role] = row
        for role in ROLES:
            path = roles.get(role)
            row = by_role.get(role)
            if not row:
                problems.append(f"Receipt missing artifact role: {role}")
                continue
            if row.get("profile") != "release":
                problems.append(f"Artifact {role} is not recorded as Release")
                continue
            expected = row.get("sha256")
            if not isinstance(expected, str) or not SHA_RE.fullmatch(expected):
                problems.append(f"Artifact {role} has invalid receipt SHA-256")
                continue
            if not isinstance(path, Path) or not path.is_file():
                problems.append(f"Artifact {role} missing on disk")
                continue
            actual = sha256_file(path)
            matched = actual.lower() == expected.lower()
            result["artifacts"][role] = {"sha256": actual, "matched": matched, "profile": "release"}
            if not matched:
                problems.append(f"Artifact {role} SHA-256 does not match receipt")
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
        problems.append(f"Receipt unavailable/invalid: {type(exc).__name__}: {exc}")
    result["state"] = "verified" if not problems else "blocked"
    return result


def checkout_head(cfg: dict) -> str | None:
    repo = cfg.get("repo_root")
    if not isinstance(repo, str) or not repo:
        return None
    try:
        p = subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"],
                           timeout=5, capture_output=True, text=True, check=True)
        head = p.stdout.strip()
        return head if COMMIT_RE.fullmatch(head) else None
    except (OSError, subprocess.SubprocessError):
        return None


def source_preflight(source: str, engine_period: str, capture_policy: str, report: dict) -> list[str]:
    issues = []
    if source not in SOURCES:
        return ["Unknown audio source selection"]
    if engine_period not in POLICIES or capture_policy not in POLICIES:
        return ["Unknown Windows period/capture policy"]
    caps = report.get("capabilities", {})
    if engine_period not in (caps.get("windows_engine_period_policies") or []):
        issues.append(f"Unreceipted Windows engine period policy: {engine_period}")
    if capture_policy not in (caps.get("windows_capture_policies") or []):
        issues.append(f"Unreceipted Windows capture policy: {capture_policy}")
    if source == "deskfuse":
        # Product enrollment is an AES-GCM credential. Lab QUIC uses mTLS;
        # comparing DER to a file says nothing about enrollment ownership.
        issues.append("Dedicated endpoint requires Codex Ultra's authenticated enrollment-to-mTLS binding; manually entered peer hashes are forbidden")
    return issues


def source_comparison() -> list[dict]:
    return [
        {"id": "mm1", "name": "MM-1 / physical loopback", "state": "runner_supported", "boundary": "session", "impacts": ["latency", "stability"],
         "note": "Protected capture reference; application output must be explicitly routed to MM-1."},
        {"id": "deskfuse", "name": "DeskFuse own endpoint / software loopback", "state": "blocked_identity", "boundary": "system", "impacts": ["latency", "stability", "privacy"],
         "note": "Native runner exists; lab launch blocked pending authenticated product enrollment-to-client-certificate binding. Requires explicit application routing, never automatic default restoration."},
        {"id": "direct_export", "name": "Direct WaveRT PCM export", "state": "future", "boundary": "build", "impacts": ["latency", "cpu"],
         "note": "Not implemented at audited commit. Driver timing and export remain Codex Ultra's work."},
    ]
