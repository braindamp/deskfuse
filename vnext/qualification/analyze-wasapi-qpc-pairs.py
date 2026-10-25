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
                # Smaller original packets are valid trace observations but
                # cannot contain the two complete 240-frame fragments here.
                if 0 < int(row['packet_frames']) < 480:
                    continue
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
    compared_pairs = 0
    unmatched_sampled_pairs = 0
    examples = []
    conflict_examples = []
    sender_first_frames = []
    log = sender_log.read_text(encoding="utf-8-sig", errors="replace")
    for match in SENDER_PAIR.finditer(log):
        first, second, first_qpc, second_qpc, first_now, second_now, first_decision, second_decision, _ = match.groups()
        if not (first_decision.startswith("Fresh")
                and second_decision == "FutureTimestamp"):
            continue
        sender_pairs += 1
        first, second, first_qpc, second_qpc = map(
            int, (first, second, first_qpc, second_qpc))
        sender_first_frames.append(first)

        # Match both fragments against the ORIGINAL GetBuffer span, not
        # merely its first 240-frame pair. Large OS packets can contain
        # 3+ fragments. Do not match a pair that straddles two packets.
        containing = [
            p for p in packets.values()
            if p["first_frame"] <= first
            and second == first + 240
            and first + 480 <= p["first_frame"] + p["packet_frames"]
            and (first - p["first_frame"]) % 240 == 0
        ]
        if not containing:
            unmatched_sampled_pairs += 1
            continue  # Two independently sampled logs may not overlap.
        compared_pairs += 1
        packet = containing[0]
        offset = first - packet["first_frame"]
        packet_qpc = packet["packet_qpc100"]
        expected_first_qpc = (
            0 if packet_qpc == 0 else
            packet_qpc + offset * 10_000_000 // 48_000)
        expected_second_qpc = (
            0 if packet_qpc == 0 else
            packet_qpc + (offset + 240) * 10_000_000 // 48_000)
        if (first_qpc != expected_first_qpc
                or second_qpc != expected_second_qpc):
            mismatched += 1
            if len(conflict_examples) < 4:
                conflict_examples.append({
                    "first_frame": first,
                    "original_packet_frame": packet["first_frame"],
                    "expected_first_qpc100": expected_first_qpc,
                    "observed_first_qpc100": first_qpc,
                    "expected_second_qpc100": expected_second_qpc,
                    "observed_second_qpc100": second_qpc,
                })
            continue
        exact_match += 1
        if len(examples) < 4:
            examples.append({
                "first_frame": first,
                "second_frame": second,
                "original_packet_first_frame": packet["first_frame"],
                "offset_inside_original_packet": offset,
                "capture_first_lead_100ns":
                    first_qpc - packet["local_getbuffer_qpc100"],
                "capture_second_lead_100ns":
                    second_qpc - packet["local_getbuffer_qpc100"],
                "sender_first_lead_100ns": first_qpc - int(first_now),
                "sender_second_lead_100ns": second_qpc - int(second_now),
            })
    sampled_span = (
        (min(packets), max(p["first_frame"] + p["packet_frames"]
                           for p in packets.values())) if packets else None
    )
    sender_span = (
        (min(sender_first_frames), max(sender_first_frames) + 480)
        if sender_first_frames else None
    )
    return {
        "sampled_original_packets": len(packets),
        "malformed_original_rows": malformed,
        "original_packet_cutoff_straddles_at_capture":
            first_fresh_second_future_at_capture,
        "sender_fresh_then_future_pairs": sender_pairs,
        "sender_pairs_within_sampled_packet": compared_pairs,
        "sender_pairs_outside_sampled_packet_samples": unmatched_sampled_pairs,
        "sender_pairs_matching_original_packet_exactly": exact_match,
        "sender_pairs_conflicting_with_original_packet": mismatched,
        "sampled_original_frame_span": sampled_span,
        "logged_sender_pair_frame_span": sender_span,
        "examples": examples,
        "conflicts": conflict_examples,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path, help="one completed silent evidence folder")
    args = parser.parse_args()
    report = analyze(args.folder)
    for name, value in report.items():
        if name not in ("examples", "conflicts"):
            print(f"{name}={value}")
    for example in report["examples"]:
        print("exact_original_packet_match " +
              " ".join(f"{key}={value}" for key, value in example.items()))
    for example in report["conflicts"]:
        print("original_packet_qpc_CONFLICT " +
              " ".join(f"{key}={value}" for key, value in example.items()))
    if report["sender_pairs_conflicting_with_original_packet"]:
        print("CONCLUSION=CONFLICT: sampled original packet and sender QPC "
              "disagree on a shared source-frame interval. Review raw logs.")
    elif report["sender_pairs_matching_original_packet_exactly"]:
        print("CONCLUSION=EXACT_MATCH: sampled original GetBuffer packet "
              "contains a 240-frame QPC-pass / QPC-future-reject pair.")
    else:
        print("CONCLUSION=NO_SAMPLE_OVERLAP: sender mixed-decision pairs do "
              "not fall wholly inside sampled original GetBuffer packets. "
              "This is NOT evidence against the timestamp-cutoff hypothesis.")
    print("NOTE=Rate-limited diagnostic samples; never complete frame accounting.")


if __name__ == "__main__":
    main()
