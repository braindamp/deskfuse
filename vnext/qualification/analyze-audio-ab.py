#!/usr/bin/env python3
"""Compare audible, physical DeskFuse A/B sessions. Never infer heard sound.

Only local A/B logs (no keys or PCM) are read. Versions have different
diagnostic schemas; absent metrics remain null, not zero. QUIC transport loss
and source-frame omissions are separate observations, not interchangeable.
"""
from __future__ import annotations

import argparse
import json
import re
from collections import defaultdict
from pathlib import Path

KEY = re.compile(r"\b([a-zA-Z_][a-zA-Z_0-9]*)=([^\s]+)")
HEX40 = re.compile(r"^[0-9a-f]{40}$")


def parse_fields(line: str | None) -> dict[str, str]:
    return dict(KEY.findall(line or ""))


def last_line(text: str, prefix: str) -> str | None:
    return next((line for line in reversed(text.splitlines()) if line.startswith(prefix)), None)


def number(fields: dict[str, str], name: str) -> int | None:
    try:
        return int(fields[name])
    except (KeyError, TypeError, ValueError):
        return None


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace") if path.exists() else ""


def load_json(path: Path) -> dict | None:
    try:
        data = json.loads(read(path))
        return data if isinstance(data, dict) else None
    except (OSError, ValueError):
        return None


def evaluate(folder: Path) -> dict:
    session = load_json(folder / "run.json")
    pair = load_json(folder / "pair.json")
    if not session or not pair:
        return {"folder": folder.name, "status": "INVALID_EVIDENCE", "reason": "run.json/pair.json missing"}
    windows = read(folder / "windows-source.stdout.txt")
    errors = read(folder / "windows-source.stderr.txt")
    android = read(folder / "android-receiver.txt")
    ws = parse_fields(last_line(windows, "DONE Windows WASAPI source"))
    ads = parse_fields(last_line(android, "DONE Android sink"))
    aaudio = parse_fields(last_line(android, "AUDIO Android speaker"))
    native = parse_fields(last_line(errors, "DONE WASAPI capture"))
    manifest = {
        "folder": folder.name,
        "slot": session.get("slot"),
        "commit": session.get("commit"),
        "processing_mode": session.get("processing_mode"),
        "client_mode": session.get("client_mode"),
        "seconds": session.get("seconds"),
        "started_utc": session.get("started_utc"),
        "source_auth": "AUTH Windows:" in windows,
        "sink_auth": "AUTH Android:" in android,
        "source_done": bool(ws),
        "sink_done": bool(ads),
        "native_capture_done": bool(native),
        "windows_exit": session.get("windows_exit"),
        "android_exit": session.get("android_exit"),
        "received": number(ads, "received"),
        "missing_sequences": number(ads, "missing_between"),
        "source_missing_frames": number(ads, "source_missing_frames"),
        "source_missing_ms": None,
        "stale": number(ads, "stale"),
        "quic_lost_receiver": number(ads, "quic_lost"),
        "pcm_queued": number(ws, "real_pcm_blocks_queued"),
        "source_queue_old": number(ws, "source_queue_old"),
        "writer_drops": number(native, "dropped_blocks_bounded_writer"),
        "pre_quic_source_gaps": number(ws, "pre_quic_source_gaps"),
        "pre_quic_missing_frames": number(ws, "pre_quic_missing_frames"),
        "quiche_queue_full": number(ws, "quiche_queue_full"),
        "qpc_expired": number(ws, "qpc_expired"),
        "qpc_future": number(ws, "qpc_future"),
        "qpc_proven_fragments": number(ws, "qpc_proven_fragments"),
        "plaintext_expired": number(ws, "quiche_plaintext_expired"),
        "capture_discontinuities": number(ws, "capture_discontinuities"),
        "rendered_frames": number(aaudio, "rendered_frames"),
        "accepted_frames": number(aaudio, "accepted_frames"),
        "silence_frames": number(aaudio, "silence_frames"),
        "underrun_events": number(aaudio, "underrun_events"),
        "reprime_events": number(aaudio, "reprime_events"),
        "stream_error": number(aaudio, "stream_error"),
        "listener_heard": None,
        "listener_rating": None,
        "status": "UNKNOWN",
        "reasons": [],
    }
    note = load_json(folder / "listening.json")
    if note:
        manifest["listener_heard"] = note.get("heard")
        manifest["listener_rating"] = note.get("rating_1_to_5")
        manifest["listener_notes"] = note.get("notes")
    missing = manifest["source_missing_frames"]
    if missing is not None:
        # Frame rate is fixed 48000 Hz in both current lab builds.
        manifest["source_missing_ms"] = missing / 48
    reasons = []
    if not HEX40.fullmatch(str(manifest["commit"])) or manifest["commit"] != pair.get("commit"):
        reasons.append("commit identity missing or mismatch")
    if manifest["slot"] != pair.get("slot") or manifest["client_mode"] != pair.get("mode"):
        reasons.append("pair identity/mode mismatch")
    if manifest["processing_mode"] != ("reference-native" if pair.get("mode") == "client-playback"
                                      else manifest["processing_mode"]):
        reasons.append("processing mode incompatible with receiver")
    if manifest["windows_exit"] != 0 or manifest["android_exit"] != 0:
        reasons.append("native process exit nonzero/unknown")
    if not (manifest["source_auth"] and manifest["sink_auth"]):
        reasons.append("both-sided mutual TLS not evidenced")
    if not (manifest["source_done"] and manifest["sink_done"]):
        reasons.append("incomplete observation window")
    if not aaudio or manifest["rendered_frames"] is None or manifest["rendered_frames"] <= 0:
        reasons.append("no recorded AAudio output")
    if manifest["stream_error"] != 0:
        reasons.append("AAudio stream error or unavailable status")
    if manifest["listener_heard"] == "not-heard":
        reasons.append("operator reported no audible sound")
    manifest["reasons"] = reasons
    if reasons:
        manifest["status"] = "FAIL"
    elif manifest["listener_heard"] == "heard":
        manifest["status"] = "HEARD_WITH_GAPS" if any(
            (manifest[key] or 0) > 0 for key in
            ("source_missing_frames", "missing_sequences", "underrun_events")
        ) else "HEARD_NO_REPORTED_GAPS"
    else:
        manifest["status"] = "OUTPUT_RECORDED_LISTENING_UNCONFIRMED"
    return manifest


def aggregate(rows: list[dict]) -> list[dict]:
    groups = defaultdict(list)
    for row in rows:
        if row.get("commit") and row.get("processing_mode"):
            groups[(row["slot"], row["commit"], row["processing_mode"])].append(row)
    summary = []
    for (slot, commit, mode), trials in groups.items():
        def numeric(key: str):
            present = [r[key] for r in trials if isinstance(r.get(key), (float, int))]
            return None if not present else round(sum(present) / len(present), 2)
        summary.append({
            "slot": slot, "commit": commit, "mode": mode,
            "trials": len(trials), "failed": sum(r.get("status") == "FAIL" for r in trials),
            "avg_source_missing_ms": numeric("source_missing_ms"),
            "avg_missing_sequences": numeric("missing_sequences"),
            "avg_rendered_frames": numeric("rendered_frames"),
            "avg_underruns": numeric("underrun_events"),
            "human_heard": sum(r.get("listener_heard") == "heard" for r in trials),
        })
    return summary


def fmt(value) -> str:
    return "N/A" if value is None else str(value)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runs", type=Path, help="A/B runs directory (local logs only)")
    args = parser.parse_args()
    if not args.runs.is_dir():
        parser.error("No A/B physical runs directory")
    rows = [
        evaluate(folder)
        for folder in sorted(args.runs.iterdir())
        if folder.is_dir() and (folder / "run.json").is_file()
    ]
    if not rows:
        print("No recorded A/B physical runs yet.")
        return 0
    print("A/B AUDIBLE SESSION COMPARISON (not an acoustic measurement)")
    print("Time/slot/commit/mode                     auth result                            recv seqMissing srcMissingMs render underruns listen")
    for r in rows:
        if r["status"] == "INVALID_EVIDENCE":
            print(f"{r['folder']} INVALID_EVIDENCE")
            continue
        ident = "{} {} {} {}".format(
            (r.get("started_utc") or "")[:16], r.get("slot"), str(r.get("commit"))[:9],
            r.get("processing_mode"))
        print("{:<43} {:<34} {:>5} {:>10} {:>12} {:>7} {:>9} {}".format(
            ident, r["status"], fmt(r["received"]), fmt(r["missing_sequences"]),
            fmt(r["source_missing_ms"]), fmt(r["rendered_frames"]),
            fmt(r["underrun_events"]), fmt(r["listener_heard"])))
        if r["reasons"]:
            print("   FAIL: " + "; ".join(r["reasons"]))
    print("\nBY EXACT COMMIT AND PROCESSING MODE (means, not matched experimental inference)")
    for item in aggregate(rows):
        print(" {} {} {} trials={} failed={} missing_ms={} missing_seq={} renders={} underruns={} heard={}".format(
            item["slot"], item["commit"][:9], item["mode"], item["trials"], item["failed"],
            fmt(item["avg_source_missing_ms"]), fmt(item["avg_missing_sequences"]),
            fmt(item["avg_rendered_frames"]), fmt(item["avg_underruns"]), item["human_heard"]))
    print("\nInterpretation: source-frame gaps and QUIC loss are distinct. "
          "Old revisions do not expose all metrics; N/A does not mean zero. "
          "AAudio rendered frames are NOT proof of audible speaker output. "
          "Record listening with -Action Rate.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
