#!/usr/bin/env python3
"""Offline exact packet-origin versus sender-QPC regression; no devices."""
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).parent
spec = importlib.util.spec_from_file_location(
    "wasapi_pair_report", HERE / "analyze-wasapi-qpc-pairs.py")
assert spec is not None and spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PairedFragmentEvidenceTests(unittest.TestCase):
    def sample(self, root: Path, *, sender_second_qpc: int = 1_145_650) -> None:
        (root / "windows-original-wasapi-packets.csv").write_text(
            "first_frame,packet_frames,flags,packet_qpc100,"
            "local_getbuffer_qpc100,first_fragment_frame,first_fragment_qpc100,"
            "second_fragment_frame,second_fragment_qpc100,"
            "first_future_100ns,second_future_100ns\n"
            "1000,480,0,1095650,1000000,1000,1095650,1240,1145650,95650,145650\n",
            encoding="utf-8",
        )
        (root / "windows-source.stderr.txt").write_text(
            "TRACE Windows ADJACENT_240_QPC_PAIR "
            "first_frame=1000 second_frame=1240 first_qpc100=1095650 "
            f"second_qpc100={sender_second_qpc} first_now_qpc100=1000000 "
            "second_now_qpc100=1005000 first_decision=Fresh { age_100ns: 0 } "
            "second_decision=FutureTimestamp mixed_future_pairs=1\n",
            encoding="utf-8",
        )

    def test_matches_same_original_packet_and_proves_cutoff_straddle(self):
        with tempfile.TemporaryDirectory() as t:
            path = Path(t)
            self.sample(path)
            data = module.analyze(path)
            self.assertEqual(data["sampled_original_packets"], 1)
            self.assertEqual(data["original_packet_cutoff_straddles_at_capture"], 1)
            self.assertEqual(data["sender_fresh_then_future_pairs"], 1)
            self.assertEqual(data["sender_pairs_matching_original_packet_exactly"], 1)
            self.assertEqual(data["sender_pairs_conflicting_with_original_packet"], 0)
            self.assertEqual(data["examples"][0]["sender_second_lead_100ns"], 140650)

    def test_rejects_false_provenance_match(self):
        with tempfile.TemporaryDirectory() as t:
            path = Path(t)
            self.sample(path, sender_second_qpc=1_150_650)
            data = module.analyze(path)
            self.assertEqual(data["sender_fresh_then_future_pairs"], 1)
            self.assertEqual(data["sender_pairs_matching_original_packet_exactly"], 0)
            self.assertEqual(data["sender_pairs_conflicting_with_original_packet"], 1)

    def test_incomplete_session_has_no_invented_success(self):
        with tempfile.TemporaryDirectory() as t:
            with self.assertRaises(FileNotFoundError):
                module.analyze(Path(t))


if __name__ == "__main__":
    unittest.main()
