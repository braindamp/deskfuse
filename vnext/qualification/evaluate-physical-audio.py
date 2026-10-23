#!/usr/bin/env python3
"""Evaluate actual CP-001/MP-001 session logs without manufacturing acceptance.

Physical evidence lives outside the repository. This checks log consistency
only. Acoustic latency, operator Brave restorations, Wi-Fi impairment exposure,
and externally measured gains cannot be inferred from PCM/transport counters.
"""
from __future__ import annotations

import argparse
import json
import re
from collections import defaultdict
from pathlib import Path

METRIC = re.compile(r"\b([a-zA-Z_][a-zA-Z_0-9]*)=([^\s]+)")
SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")
REQUIRED = {
    "windows-source.stdout.txt",
    "windows-source.stderr.txt",
    "android-receiver.txt",
    "session.txt",
    "android-sha256.txt",
}


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace") if path.exists() else ""


def metrics(line: str) -> dict[str, str]:
    return {name: value for name, value in METRIC.findall(line)}


def line_with_prefix(text: str, prefix: str) -> str | None:
    return next((line for line in text.splitlines() if line.startswith(prefix)), None)


def integer(field: dict[str, str], name: str) -> int | None:
    try:
        return int(field[name])
    except (KeyError, ValueError):
        return None


def check_session(folder: Path) -> dict:
    manifest = read(folder / "session.txt")
    windows = read(folder / "windows-source.stdout.txt")
    err = read(folder / "windows-source.stderr.txt")
    phone = read(folder / "android-receiver.txt")
    kv = metrics(manifest.replace("\n", " "))
    missing = sorted(name for name in REQUIRED if not (folder / name).exists())
    failures: list[str] = []
    pending: list[str] = []
    if missing:
        failures.append("Missing session artifacts: " + ", ".join(missing))
    if kv.get("android_pair_sha256_verified") != "true":
        failures.append("Android executable/native library deployment hashes not verified")
    if not re.fullmatch(r"[0-9a-f]{40}", kv.get("git_commit", "")):
        failures.append("Missing exact 40-character source commit")
    hashes = [line for line in manifest.splitlines() if line.startswith("sha256=")]
    if len(hashes) != 4 or any(not SHA256.fullmatch(line[7:71]) for line in hashes):
        failures.append("Expected exactly four local binary SHA256 entries")
    if kv.get("sender_exit") != "0":
        failures.append("Windows sender did not report clean exit")
    if kv.get("client_exit") != "0":
        failures.append("Android receiver did not report clean exit")
    if line_with_prefix(windows, "READY continuous Windows source ") is None:
        failures.append("Windows source READY not observed")
    if line_with_prefix(windows, "AUTH Windows:") is None:
        failures.append("Authenticated Windows peer not observed")
    if line_with_prefix(phone, "AUTH Android:") is None:
        failures.append("Authenticated Android server not observed")
    # Native WASAPI emits its lifecycle counters on the child stderr,
    # inherited by the Rust QUIC source. Never report a false missing capture
    # merely because the Rust parent redirects stdout separately.
    capture = (line_with_prefix(err, "DONE WASAPI capture ") or
               line_with_prefix(windows, "DONE WASAPI capture "))
    sender = line_with_prefix(windows, "DONE Windows WASAPI source ")
    received = line_with_prefix(phone, "DONE Android sink ")
    speaker = line_with_prefix(phone, "AUDIO Android speaker ")
    if any(x is None for x in (capture, sender, received, speaker)):
        failures.append("Missing final WASAPI/QUIC/Android AAudio counters")
    capture_fields = metrics(capture or "")
    sender_fields = metrics(sender or "")
    receiver_fields = metrics(received or "")
    speaker_fields = metrics(speaker or "")
    if integer(capture_fields, "source_frames_observed") in (None, 0):
        failures.append("No valid WASAPI source frames observed")
    if integer(sender_fields, "real_pcm_blocks_queued") in (None, 0):
        failures.append("No Windows PCM queued to quiche")
    if integer(receiver_fields, "received") in (None, 0):
        failures.append("No authenticated PCM received by Android")
    if integer(speaker_fields, "rendered_frames") in (None, 0):
        failures.append("No source PCM rendered by AAudio")
    if integer(speaker_fields, "stream_error") != 0:
        failures.append("AAudio output stream reported an error or no terminal status")
    if receiver_fields.get("mode") != "native-speaker":
        failures.append("Android did not confirm native speaker playback mode")
    if "STRICT PCM failure" in phone or "STRICT PCM violation" in phone:
        failures.append("Strict PCM verifier reported failure")
    if "panicked at" in phone or "panicked at" in windows or "panicked at" in err:
        failures.append("Runtime panic in audio session")
    duration = integer(kv, "duration_seconds")
    if not duration or duration < 1800:
        pending.append("This is not a preregistered 30-minute exposure")
    if kv.get("brave_restoration_at_5min") == "OPERATOR_REQUIRED":
        pending.append("Brave restoration at minute 5 not independently evidenced")
    if kv.get("brave_restoration_at_20min") == "OPERATOR_REQUIRED":
        pending.append("Brave restoration at minute 20 not independently evidenced")
    if kv.get("competing_load", "").startswith("NONE"):
        pending.append("Matched competing-load and pressure exposure not evidenced")
    pending.append("Acoustic capture-to-sound latency needs external measured evidence")
    pending.append("Playback volume/mute operations and audible pops require operator measurements")
    return {
        "folder": str(folder),
        "commit": kv.get("git_commit"),
        "profile": kv.get("profile"),
        "mode": kv.get("modes"),
        "strict": kv.get("strict_pcm"),
        "duration_s": duration,
        "session_log_consistency": "FAIL" if failures else "PASS",
        "physical_acceptance": "OPEN",
        "observations": {
            "windows_source_frames": integer(capture_fields, "source_frames_observed"),
            "windows_writer_drops": integer(capture_fields, "dropped_blocks_bounded_writer"),
            "windows_quic_queue_full": integer(sender_fields, "quiche_queue_full"),
            "windows_qpc_expired": integer(sender_fields, "qpc_expired"),
            "windows_plaintext_expired": integer(sender_fields, "quiche_plaintext_expired"),
            "android_packets": integer(receiver_fields, "received"),
            "android_missing_source_frames": integer(receiver_fields, "source_missing_frames"),
            "aaudio_rendered_frames": integer(speaker_fields, "rendered_frames"),
            "aaudio_silence_frames_total": integer(speaker_fields, "silence_frames"),
            "aaudio_underrun_events": integer(speaker_fields, "underrun_events"),
            "aaudio_stream_error": integer(speaker_fields, "stream_error"),
        },
        "failures": failures,
        "qualification_still_required": pending,
    }


def summarize(root: Path) -> dict:
    folders = [root] if (root / "session.txt").exists() else sorted(
        path for path in root.iterdir() if path.is_dir()
    )
    sessions = [check_session(path) for path in folders if (path / "session.txt").exists()]
    groups: dict[tuple[str, str], int] = defaultdict(int)
    for session in sessions:
        if (session["session_log_consistency"] == "PASS" and
                (session["duration_s"] or 0) >= 1800):
            groups[(session["profile"] or "unknown", session["mode"] or "unknown")] += 1
    return {
        "sessions": sessions,
        "completed_30min_log_consistent_by_profile_mode": {
            f"{profile}:{mode}": n for (profile, mode), n in sorted(groups.items())
        },
        "three_independent_runs_per_profile": {
            f"{profile}:{mode}": "LOG_COUNT_ONLY" if n >= 3 else "MISSING"
            for (profile, mode), n in sorted(groups.items())
        },
        "preregistered_acceptance": "OPEN — independent Brave/load, physical, acoustic, and controlled comparison evidence required",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence_folder", type=Path)
    args = parser.parse_args()
    if not args.evidence_folder.exists() or not args.evidence_folder.is_dir():
        parser.error("evidence_folder must be an existing local session or evidence directory")
    report = summarize(args.evidence_folder)
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report["sessions"] and all(
        session["session_log_consistency"] == "PASS" for session in report["sessions"]
    ) else 1


if __name__ == "__main__":
    raise SystemExit(main())
