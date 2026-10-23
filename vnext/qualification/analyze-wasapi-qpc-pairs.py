#!/usr/bin/env python3
"""Correlate silent Windows WASAPI original-packet QPC with sender admission.

Requires a single -VerifyOnly evidence folder. This compares only exact
original source positions and QPC values; rate-limited samples cannot prove
complete stream accounting. Never reads PCM, certificates or private keys.
"""
from __future__ import annotations

import argparse
import csv
import re
from pathlib import Path

REQUIRED = (
    "first_frame", "packet_frames", "packet_qpc100",
    "local_getbuffer_qpc100", "first_fragment_frame",
    "first_fragment_qpc100", "second_fragment_frame",
    "second_fragment_qpc100",
)
SENDER_PAIR = re.compile(
    r"TRACE Windows ADJACENT_240_QPC_PAIR "
    r"first_frame=(\d+) second_frame=(\d+) "
    r"first_qpc100=(\d+) second_qpc100=(\d+) "
    r"first_now_qpc100=(\d+) second_now_qpc100=(\d+) "
    r"first_decision=(.*?) second_decision=(.*?) "
    r"mixed_future_pairs=(\d+)"
)
FUTURE_LIMIT_100NS = 100_000


def analyze(folder: Path) -> dict:
    packet_csv = folder / "windows-original-wasapi-packets.csv"
    sender_log = folder / "windows-source.stderr.txt"
    if not packet_csv.is_file() or not sender_log.is_file():
        raise FileNotFoundError(
            "Missing original-packet CSV or Windows stderr; use one new "
            "-VerifyOnly session built after packet-trace instrumentation."
        )

    packets: dict[int, dict[str, int]] = {}
    malformed = 0
    with packet_csv.open(newline="", encoding="utf-8-sig") as stream:
        reader = csv.DictReader(stream)
        if not set(REQUIRED).issubset(reader.fieldnames or []):
            raise ValueError("original WASAPI packet CSV schema incomplete")
        for row in reader:
            try:
                p = {name: int(row[name]) for name in REQUIRED}
                if (p["packet_frames"] < 480
                    or p["first_frame"] != p["first_fragment_frame"]
                    or p["first_frame"] + 240 != p["second_fragment_frame"]
                    or p["packet_qpc100"] != p["first_fragment_qpc100"]
                    or (p["packet_qpc100"] != 0 and
                        p["packet_qpc100"] + 50_000 != p["second_fragment_qpc100"])):
                    malformed += 1
                    continue
                packets[p["first_frame"]] = p
            except (TypeError, ValueError, KeyError):
                malformed += 1

    first_fresh_second_future_at_capture = 0
    for p in packets.values():
        first_lead = p["first_fragment_qpc100"] - p["local_getbuffer_qpc100"]
        second_lead = p["second_fragment_qpc100"] - p["local_getbuffer_qpc100"]
        if 0 < first_lead <= FUTURE_LIMIT_100NS < second_lead:
            first_fresh_second_future_at_capture += 1

    sender_pairs = 0
    exact_match = 0
    mismatched = 0
    examples = []
    log = sender_log.read_text(encoding="utf-8-sig", errors="replace")
    for match in SENDER_PAIR.finditer(log):
        first, second, first_qpc, second_qpc, first_now, second_now, first_decision, second_decision, _ = match.groups()
        if not (first_decision.startswith("Fresh")
                and second_decision == "FutureTimestamp"):
            continue
        sender_pairs += 1
        first, second, first_qpc, second_qpc = map(int, (first, second, first_qpc, second_qpc))
        packet = packets.get(first)
        if packet is None:
            continue  # Native trace is intentionally sampled, not exhaustive.
        if (packet["second_fragment_frame"] != second
                or packet["first_fragment_qpc100"] != first_qpc
                or packet["second_fragment_qpc100"] != second_qpc):
            mismatched += 1
            continue
        exact_match += 1
        if len(examples) < 4:
            examples.append({
                "first_frame": first,
                "second_frame": second,
                "capture_first_lead_100ns":
                    first_qpc - packet["local_getbuffer_qpc100"],
                "capture_second_lead_100ns":
                    second_qpc - packet["local_getbuffer_qpc100"],
                "sender_first_lead_100ns": first_qpc - int(first_now),
                "sender_second_lead_100ns": second_qpc - int(second_now),
            })
    return {
        "sampled_original_packets": len(packets),
        "malformed_original_rows": malformed,
        "original_packet_cutoff_straddles_at_capture":
            first_fresh_second_future_at_capture,
        "sender_fresh_then_future_pairs": sender_pairs,
        "sender_pairs_matching_original_packet_exactly": exact_match,
        "sender_pairs_conflicting_with_original_packet": mismatched,
        "examples": examples,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path, help="one completed silent evidence folder")
    args = parser.parse_args()
    report = analyze(args.folder)
    for name, value in report.items():
        if name != "examples":
            print(f"{name}={value}")
    for example in report["examples"]:
        print("exact_original_packet_match " +
              " ".join(f"{key}={value}" for key, value in example.items()))
    if report["sender_pairs_matching_original_packet_exactly"]:
        print("CONCLUSION=Original GetBuffer packet provenance matches a "
              "240-frame QPC-pass / 240-frame QPC-future-reject pair.")
    else:
        print("CONCLUSION=INCONCLUSIVE: trace sampling did not match an "
              "original 480-frame packet to both sender decisions. "
              "Do not change source timing based on this report alone.")
    print("NOTE=Rate-limited diagnostic samples; never complete frame accounting.")


if __name__ == "__main__":
    main()
