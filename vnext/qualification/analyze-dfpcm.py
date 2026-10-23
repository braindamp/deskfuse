#!/usr/bin/env python3
"""Inspect original Windows WASAPI IPC capture file without reinterpreting
source QPC as Android/acoustic time. Streaming memory, stdlib only.
Input .dfpcm contains local user audio: never upload it into CI/repository.
"""
import argparse
import array
import json
import math
import pathlib
import struct
import sys

HEADER = struct.Struct("<4sIQQIIII")
RATE = 48000
AUDIO_CHANNELS = 2
QPC_UNITS_PER_SEC = 10_000_000


def analyze(path):
    result = dict(
        records=0, source_frames=0, first_source_frame=None,
        last_source_frame=None, source_gaps=0, overlapping_source_frames=0,
        discontinuity_flags=0, silence_flags=0, timestamp_error_flags=0,
        missing_qpc=0, nonmonotonic_qpc=0, max_qpc_slope_error_us=0.0,
        pcm_clipped_samples=0, sample_peak=0,
        valid_pcm_samples=0, stereo_differing_frames=0,
        rms_100ms=[], quality="UNQUALIFIED_RAW_CAPTURE"
    )
    prev_end = None
    prev_qpc = None
    prev_source = None
    samples_squared = 0
    window_samples = 0
    active_bucket = 0

    with open(path, "rb", buffering=256 * 1024) as src:
        while True:
            header = src.read(HEADER.size)
            if not header:
                break
            if len(header) != HEADER.size:
                raise ValueError("truncated DFWC header")
            magic, version, pos, qpc, frames, flags, payload_size, reserved = (
                HEADER.unpack(header)
            )
            # Read-only offline evidence supports both immutable baseline
            # v1 (reserved must be zero) and provenance-bearing v2 records.
            # The live Rust reader accepts ONLY v2 to fail closed on mixed
            # native binaries. 40-byte local header and PCM remain unchanged.
            packet_offset = reserved
            malformed_origin = (version == 2 and (
                packet_offset > 9600 or packet_offset % 240 != 0
                or pos < packet_offset
                or (qpc != 0 and
                    qpc < packet_offset * QPC_UNITS_PER_SEC // RATE)
            ))
            if (magic != b"DFWC" or version not in (1, 2)
                    or (version == 1 and reserved != 0)
                    or malformed_origin
                    or not 1 <= frames <= 240 or payload_size != frames * 4):
                raise ValueError(
                    f"invalid DFWC record at index={result['records']}"
                )
            payload = src.read(payload_size)
            if len(payload) != payload_size:
                raise ValueError("truncated PCM data")
            if result["first_source_frame"] is None:
                result["first_source_frame"] = pos
            if prev_end is not None:
                if pos < prev_end:
                    result["overlapping_source_frames"] += prev_end - pos
                elif pos > prev_end:
                    result["source_gaps"] += pos - prev_end
            result["last_source_frame"] = pos + frames - 1
            prev_end = pos + frames

            if flags & 1:
                result["discontinuity_flags"] += 1
            if flags & 2:
                result["silence_flags"] += 1
            if flags & 4:
                result["timestamp_error_flags"] += 1

            if qpc == 0:
                result["missing_qpc"] += 1
            else:
                if prev_qpc is not None and prev_source is not None:
                    if qpc <= prev_qpc:
                        result["nonmonotonic_qpc"] += 1
                    else:
                        expected = ((pos - prev_source) * QPC_UNITS_PER_SEC / RATE)
                        observed = qpc - prev_qpc
                        result["max_qpc_slope_error_us"] = max(
                            result["max_qpc_slope_error_us"],
                            abs(observed - expected) / 10.0
                        )
                prev_qpc = qpc
                prev_source = pos

            samples = array.array("h")
            samples.frombytes(payload)
            if sys.byteorder != "little":
                samples.byteswap()
            result["sample_peak"] = max(
                result["sample_peak"], max((abs(x) for x in samples), default=0)
            )
            result["pcm_clipped_samples"] += sum(
                x <= -32768 or x >= 32767 for x in samples
            )
            result["stereo_differing_frames"] += sum(
                samples[i] != samples[i+1] for i in range(0, len(samples), 2)
            )
            result["valid_pcm_samples"] += len(samples)
            result["records"] += 1
            result["source_frames"] += frames
            for sample in samples:
                samples_squared += sample * sample
                window_samples += 1
            active_bucket += frames
            if active_bucket >= RATE // 10:
                rms = math.sqrt(samples_squared / window_samples) / 32768
                # No window content or raw PCM is saved, only level statistics.
                result["rms_100ms"].append(round(rms, 6))
                active_bucket, samples_squared, window_samples = 0, 0, 0

    rms = result["rms_100ms"]
    changes = []
    for before, after in zip(rms, rms[1:]):
        if before > 0.003 and after > 0.003:
            changes.append(abs(20 * math.log10(after / before)))
    result["max_100ms_level_step_db"] = max(changes, default=0.0)
    result["exposure_seconds"] = round(result["source_frames"] / RATE, 6)
    result["gain_step_warning"] = (
        "RMS differences include application content; NOT proof of system "
        "gain or volume handling without matched constant-amplitude stimulus"
    )
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("dfpcm", type=pathlib.Path)
    parser.add_argument("--json", type=pathlib.Path,
                        help="write metadata only, never raw PCM")
    args = parser.parse_args()
    data = analyze(args.dfpcm)
    output = json.dumps(data, indent=2)
    if args.json:
        args.json.write_text(output + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
