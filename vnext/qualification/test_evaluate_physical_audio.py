import importlib.util
from pathlib import Path
import tempfile
import unittest

module_path = Path(__file__).with_name("evaluate-physical-audio.py")
spec = importlib.util.spec_from_file_location("evaluate_physical_audio", module_path)
evaluation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluation)


class EvidenceTests(unittest.TestCase):
    def create(self, folder: Path):
        folder.mkdir(parents=True, exist_ok=True)
        manifest = "\n".join([
            "start_utc=2026-10-23T09:00:00Z",
            "git_commit=" + "a" * 40,
            "profile=audio-only",
            "modes=f1c1r1",
            "strict_pcm=False",
            "duration_seconds=1800",
            "android_pair_sha256_verified=true",
            "brave_restoration_at_5min=OPERATOR_REQUIRED",
            "brave_restoration_at_20min=OPERATOR_REQUIRED",
            "competing_load=NONE unless externally measured and recorded",
            "client_exit=0",
            "sender_exit=0",
        ] + ["sha256=" + "b" * 64 + " file=test" for _ in range(4)])
        (folder / "session.txt").write_text(manifest, encoding="utf-8")
        (folder / "android-sha256.txt").write_text(
            "b" * 64 + "  /data/local/tmp/deskfuse-vnext-continuous\n",
            encoding="utf-8",
        )
        (folder / "windows-source.stdout.txt").write_text(
            "READY continuous Windows source 192.168.2.107:48235\n"
            "AUTH Windows: Android client verified; native WASAPI capture process started\n"
            "DONE Windows WASAPI source duration=1800s real_pcm_blocks_queued=1000 "
            "quiche_queue_full=4 qpc_expired=2 quiche_plaintext_expired=1\n",
            encoding="utf-8",
        )
        (folder / "windows-source.stderr.txt").write_text(
            "DONE WASAPI capture source_frames_observed=480480 "
            "dropped_blocks_bounded_writer=0\n",
            encoding="utf-8",
        )
        (folder / "android-receiver.txt").write_text(
            "AUTH Android: verified Windows server CA chain and deskfuse-lab.local\n"
            "DONE Android sink received=990 source_missing_frames=240 mode=native-speaker\n"
            "AUDIO Android speaker accepted_frames=237600 rendered_frames=237600 "
            "silence_frames=4000 underrun_events=1 stream_error=0\n",
            encoding="utf-8",
        )
        return folder

    def test_real_log_format_does_not_claim_physical_acceptance(self):
        with tempfile.TemporaryDirectory() as t:
            folder = self.create(Path(t) / "one")
            report = evaluation.check_session(folder)
            self.assertEqual(report["session_log_consistency"], "PASS")
            self.assertEqual(report["physical_acceptance"], "OPEN")
            self.assertEqual(report["observations"]["windows_source_frames"], 480480)
            self.assertEqual(report["observations"]["windows_writer_drops"], 0)
            self.assertEqual(report["observations"]["aaudio_rendered_frames"], 237600)
            self.assertTrue(any("Brave" in s for s in report["qualification_still_required"]))

    def test_bad_hash_and_failed_android_exit_fail_closed(self):
        with tempfile.TemporaryDirectory() as t:
            folder = self.create(Path(t) / "bad")
            manifest = folder / "session.txt"
            text = manifest.read_text().replace(
                "android_pair_sha256_verified=true",
                "android_pair_sha256_verified=false",
            ).replace("client_exit=0", "client_exit=255")
            manifest.write_text(text)
            report = evaluation.check_session(folder)
            self.assertEqual(report["session_log_consistency"], "FAIL")
            self.assertTrue(any("hashes" in s for s in report["failures"]))
            self.assertTrue(any("receiver" in s for s in report["failures"]))

    def test_strict_verifier_fails_even_with_clean_output(self):
        with tempfile.TemporaryDirectory() as t:
            folder = self.create(Path(t) / "strict")
            with (folder / "android-receiver.txt").open("a") as file:
                file.write("STRICT PCM failure during steady exposure\n")
            report = evaluation.check_session(folder)
            self.assertEqual(report["session_log_consistency"], "FAIL")
            self.assertTrue(any("Strict PCM" in x for x in report["failures"]))

    def test_operator_events_are_time_windowed_and_not_independent_proof(self):
        with tempfile.TemporaryDirectory() as t:
            folder = self.create(Path(t) / "events")
            import csv
            from datetime import datetime, timedelta, timezone
            start = datetime(2026, 10, 23, 9, tzinfo=timezone.utc)
            with (folder / "operator-events.csv").open("w", newline="", encoding="utf-8") as file:
                writer = csv.writer(file)
                writer.writerow(["utc", "event", "note"])
                for i in range(16):
                    writer.writerow([(start + timedelta(seconds=300+i)).isoformat(),
                                     "brave_restored", ""])
                for i in range(15):
                    writer.writerow([(start + timedelta(seconds=1200+i)).isoformat(),
                                     "brave_restored", ""])
                writer.writerow([(start + timedelta(seconds=601)).isoformat(),
                                 "volume_changed", "phone media slider"])
                writer.writerow([(start + timedelta(seconds=602)).isoformat(),
                                 "pop_heard", "short"])
            report = evaluation.check_session(folder)
            events = report["operator_observations"]
            self.assertEqual(events["brave_restored_5min"], 16)
            self.assertEqual(events["brave_restored_20min"], 15)
            self.assertEqual(events["volume_changed"], 1)
            self.assertEqual(events["pop_heard"], 1)
            self.assertEqual(report["physical_acceptance"], "OPEN")
            self.assertTrue(any("operator observations" in s for s in
                                report["qualification_still_required"]))

    def test_three_clean_runs_are_count_only_not_acceptance(self):
        with tempfile.TemporaryDirectory() as t:
            root = Path(t)
            for i in range(3):
                self.create(root / f"session{i}")
            report = evaluation.summarize(root)
            self.assertEqual(
                report["completed_30min_log_consistent_by_profile_mode"]["audio-only:f1c1r1"],
                3,
            )
            self.assertEqual(
                report["three_independent_runs_per_profile"]["audio-only:f1c1r1"],
                "LOG_COUNT_ONLY",
            )
            self.assertIn("OPEN", report["preregistered_acceptance"])

    def test_malformed_session_never_counts_as_qualified(self):
        with tempfile.TemporaryDirectory() as t:
            folder = Path(t)
            (folder / "session.txt").write_text(
                "git_commit=" + "a" * 40 + "\nduration_seconds=1800\n",
                encoding="utf-8",
            )
            report = evaluation.summarize(folder)
            self.assertEqual(report["sessions"][0]["session_log_consistency"], "FAIL")
            self.assertFalse(report["completed_30min_log_consistent_by_profile_mode"])


if __name__ == "__main__":
    unittest.main()
