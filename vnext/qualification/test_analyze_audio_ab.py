import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

module_path = Path(__file__).with_name("analyze-audio-ab.py")
spec = importlib.util.spec_from_file_location("analyze_audio_ab", module_path)
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class ABAnalysisTests(unittest.TestCase):
    def fixture(self, root: Path, *, current: bool = True):
        run = root / "audible-run"
        run.mkdir(parents=True)
        sha = "c" * 40
        slot = "candidate" if current else "reference"
        mode = "client-playback-modes" if current else "client-playback"
        flags = "f0c0r0" if current else "reference-native"
        (run / "run.json").write_text(json.dumps({
            "slot": slot, "commit": sha, "client_mode": mode,
            "processing_mode": flags, "seconds": 20,
            "windows_exit": 0, "android_exit": 0,
            "started_utc": "2026-10-23T20:00:00Z",
        }))
        (run / "pair.json").write_text(json.dumps({
            "slot": slot, "commit": sha, "mode": mode,
        }))
        windows = (
            "READY continuous Windows source\n"
            "AUTH Windows: Android client verified; native WASAPI capture process started\n"
            "DONE Windows WASAPI source duration=20s real_pcm_blocks_queued=3500 "
            "quiche_queue_full=0 source_queue_old=0 capture_discontinuities=0 "
            + ("pre_quic_missing_frames=480 qpc_proven_fragments=20 qpc_expired=3 " if current else "")
            + "quic_lost=1\n"
        )
        (run / "windows-source.stdout.txt").write_text(windows)
        (run / "windows-source.stderr.txt").write_text(
            "DONE WASAPI capture source_frames_observed=960000 dropped_blocks_bounded_writer=0\n"
        )
        (run / "android-receiver.txt").write_text(
            "AUTH Android: verified Windows server CA chain and deskfuse-lab.local\n"
            "DONE Android sink received=3450 first_seq=Some(1) last_seq=Some(3500) "
            "missing_between=50 source_missing_frames=2400 stale=0 quic_lost=2 mode=native-speaker\n"
            "AUDIO Android speaker accepted_frames=828000 rendered_frames=825600 "
            "silence_frames=1920 overflow_packets=0 callbacks=1700 queued_frames=240 "
            "stream_error=0 format=48000Hz/2ch"
            + (" underrun_events=3 reprime_events=1" if current else "") + "\n"
        )
        return run

    def test_new_mode_metrics_are_distinct_and_acoustic_unconfirmed(self):
        with tempfile.TemporaryDirectory() as d:
            folder = self.fixture(Path(d), current=True)
            result = report.evaluate(folder)
            self.assertEqual(result["status"], "OUTPUT_RECORDED_LISTENING_UNCONFIRMED")
            self.assertEqual(result["source_missing_ms"], 50)
            self.assertEqual(result["missing_sequences"], 50)
            self.assertEqual(result["pre_quic_missing_frames"], 480)
            self.assertEqual(result["qpc_proven_fragments"], 20)
            self.assertEqual(result["underrun_events"], 3)
            self.assertIsNone(result["listener_heard"])

    def test_old_version_absent_diagnostics_are_not_fake_zeroes(self):
        with tempfile.TemporaryDirectory() as d:
            folder = self.fixture(Path(d), current=False)
            result = report.evaluate(folder)
            self.assertEqual(result["status"], "OUTPUT_RECORDED_LISTENING_UNCONFIRMED")
            self.assertIsNone(result["qpc_expired"])
            self.assertIsNone(result["pre_quic_missing_frames"])
            self.assertIsNone(result["underrun_events"])
            self.assertEqual(result["rendered_frames"], 825600)

    def test_fail_closed_on_failed_auth_and_early_exit(self):
        with tempfile.TemporaryDirectory() as d:
            folder = self.fixture(Path(d))
            path = folder / "android-receiver.txt"
            path.write_text("FAIL continuous QUIC lab: Android QUIC connection closed\n")
            run_path = folder / "run.json"
            obj = json.loads(run_path.read_text())
            obj["android_exit"] = 1
            run_path.write_text(json.dumps(obj))
            result = report.evaluate(folder)
            self.assertEqual(result["status"], "FAIL")
            self.assertTrue(any("mutual TLS" in m for m in result["reasons"]))
            self.assertTrue(any("AAudio" in m for m in result["reasons"]))

    def test_listener_reporting_and_pair_identity(self):
        with tempfile.TemporaryDirectory() as d:
            folder = self.fixture(Path(d))
            (folder / "listening.json").write_text(json.dumps({
                "heard": "heard", "rating_1_to_5": 3, "notes": "occasional glitch",
            }))
            r = report.evaluate(folder)
            self.assertEqual(r["status"], "HEARD_WITH_GAPS")
            self.assertEqual(r["listener_rating"], 3)
            (folder / "listening.json").write_text(json.dumps({
                "heard": "not-heard", "rating_1_to_5": 1,
            }))
            self.assertEqual(report.evaluate(folder)["status"], "FAIL")
            pair = folder / "pair.json"
            pair.write_text(json.dumps({"slot": "candidate", "commit": "d" * 40,
                                        "mode": "client-playback-modes"}))
            self.assertIn("commit identity missing or mismatch", report.evaluate(folder)["reasons"])

    def test_strict_audible_flag_is_explicit_and_supported(self):
        with tempfile.TemporaryDirectory() as d:
            folder = self.fixture(Path(d), current=True)
            pair_path = folder / "pair.json"
            pair = json.loads(pair_path.read_text())
            pair["supports_strict"] = True
            pair_path.write_text(json.dumps(pair))
            run_path = folder / "run.json"
            run = json.loads(run_path.read_text())
            run["client_mode"] = "client-playback-strict"
            run["processing_mode"] = "strict-f0c0r0"
            run_path.write_text(json.dumps(run))
            self.assertEqual(report.evaluate(folder)["status"], "OUTPUT_RECORDED_LISTENING_UNCONFIRMED")
            pair["supports_strict"] = False
            pair_path.write_text(json.dumps(pair))
            self.assertIn("pair identity/mode mismatch", report.evaluate(folder)["reasons"])

    def test_aggregator_never_mixes_reference_and_candidate(self):
        with tempfile.TemporaryDirectory() as d:
            r1 = report.evaluate(self.fixture(Path(d) / "one", current=True))
            r2 = report.evaluate(self.fixture(Path(d) / "two", current=False))
            groups = report.aggregate([r1, r2])
            self.assertEqual(len(groups), 2)
            self.assertEqual({x["slot"] for x in groups}, {"candidate", "reference"})
            self.assertTrue(all(x["trials"] == 1 for x in groups))


if __name__ == "__main__":
    unittest.main()
