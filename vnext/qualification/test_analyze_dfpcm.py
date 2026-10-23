#!/usr/bin/env python3
"""Deterministic M1 capture record analysis; synthetic local-only files."""
import importlib.util
import pathlib
import struct
import tempfile
import unittest

MODULE = pathlib.Path(__file__).with_name("analyze-dfpcm.py")
spec = importlib.util.spec_from_file_location("analyze_dfpcm", MODULE)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


def pack(first_frame, qpc, frames=240, flags=0, sample=1000, version=1, packet_offset=0):
    pcm = struct.pack("<hh", sample, sample) * frames
    header = mod.HEADER.pack(
        b"DFWC", version, first_frame, qpc, frames, flags, len(pcm), packet_offset
    )
    return header + pcm


class CaptureEvidenceTests(unittest.TestCase):
    def run_bytes(self, data):
        with tempfile.TemporaryDirectory() as d:
            file = pathlib.Path(d) / "check.dfpcm"
            file.write_bytes(data)
            return mod.analyze(file)

    def test_contiguous_frames_and_qpc(self):
        a = pack(48000, 10_000_000)
        b = pack(48240, 10_050_000)
        result = self.run_bytes(a + b)
        self.assertEqual(result["records"], 2)
        self.assertEqual(result["source_frames"], 480)
        self.assertEqual(result["source_gaps"], 0)
        self.assertEqual(result["overlapping_source_frames"], 0)
        self.assertEqual(result["missing_qpc"], 0)
        self.assertEqual(result["nonmonotonic_qpc"], 0)
        self.assertEqual(result["max_qpc_slope_error_us"], 0)

    def test_source_gap_and_discontinuity_are_distinct(self):
        a = pack(48000, 10_000_000)
        b = pack(48720, 10_150_000, flags=1)
        result = self.run_bytes(a + b)
        self.assertEqual(result["source_gaps"], 480)
        self.assertEqual(result["discontinuity_flags"], 1)

    def test_missing_and_reversed_qpc_are_distinct(self):
        a = pack(1000, 100_000)
        b = pack(1240, 0, flags=4)
        c = pack(1480, 99_000)
        result = self.run_bytes(a + b + c)
        self.assertEqual(result["missing_qpc"], 1)
        self.assertEqual(result["timestamp_error_flags"], 1)
        self.assertEqual(result["nonmonotonic_qpc"], 1)

    def test_truncated_and_invalid_wire_rejected(self):
        with self.assertRaises(ValueError):
            self.run_bytes(pack(0, 1)[:-1])
        with self.assertRaises(ValueError):
            self.run_bytes(b"BAD!" + pack(0, 1)[4:])
        with self.assertRaises(ValueError):
            self.run_bytes(pack(0, 1) + b"DFWC")

    def test_v2_real_original_packet_fragments_preserve_frame_and_qpc_evidence(self):
        start = pack(48000, 10_000_000, version=2, packet_offset=0)
        later = pack(48240, 10_050_000, version=2, packet_offset=240)
        report = self.run_bytes(start + later)
        self.assertEqual(report["source_frames"], 480)
        self.assertEqual(report["source_gaps"], 0)
        self.assertEqual(report["nonmonotonic_qpc"], 0)
        self.assertEqual(report["max_qpc_slope_error_us"], 0)
        # Strict live sender rejects v1; the offline inspector still reads
        # immutable reference v1 artifacts without changing them.
        self.assertEqual(self.run_bytes(pack(48000, 10_000_000))["records"], 1)
        for kwargs in [
            dict(version=3), dict(version=2, packet_offset=239),
            dict(version=2, packet_offset=240),
            dict(version=2, packet_offset=9840),
        ]:
            with self.assertRaises(ValueError):
                self.run_bytes(pack(0, 10_000_000, **kwargs))

    def test_full_scale_clips_are_measured_not_interpreted_as_volume(self):
        data = pack(0, 1_000_000, sample=32767)
        result = self.run_bytes(data)
        self.assertEqual(result["pcm_clipped_samples"], 480)
        self.assertEqual(result["sample_peak"], 32767)
        self.assertEqual(result["quality"], "UNQUALIFIED_RAW_CAPTURE")


if __name__ == "__main__":
    unittest.main()
