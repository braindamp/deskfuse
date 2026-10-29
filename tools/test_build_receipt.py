"""Behavioral source/build provenance tests; no product or hardware operations."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("build-receipt.py")
spec = importlib.util.spec_from_file_location("build_receipt", SCRIPT)
receipt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(receipt)


class BuildReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Receipt Test")
        self.git("config", "user.email", "receipt@example.invalid")
        (self.root / ".gitignore").write_text("out/\n", encoding="utf-8")
        (self.root / "source.txt").write_text("baseline\n", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-qm", "fixture")
        self.out = self.root / "out"
        self.out.mkdir()
        self.baseline = self.out / "source.json"
        receipt.write_json(self.baseline, receipt.snapshot(self.root))
        self.report = self.out / "check.json"
        self.log = self.out / "check.log"

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args], stderr=subprocess.STDOUT)

    def check(self, code=0):
        return subprocess.run([sys.executable, "-B", str(SCRIPT), "check", "--root", str(self.root),
                               "--report", str(self.report), "--log", str(self.log),
                               "--name", "fixture", "--kind", "test", "--", sys.executable,
                               "-c", f"print('actual check'); raise SystemExit({code})"],
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)

    def create(self, reports=None, artifacts=None):
        return receipt.build_receipt(self.root, self.baseline, reports or [self.report],
                                     artifacts or [], "fixture", "Release", "x64")

    def test_actual_command_and_artifact_are_recorded_without_hardware_claim(self):
        self.assertEqual(self.check().returncode, 0)
        artifact = self.out / "artifact.bin"
        artifact.write_bytes(b"specific output")
        result = self.create(artifacts=[artifact])
        self.assertTrue(result["recorded_checks_passed"])
        self.assertEqual(result["artifacts"][0]["sha256"], receipt.sha256(artifact))
        self.assertEqual(result["checks"][0]["command"][-1], "print('actual check'); raise SystemExit(0)")
        self.assertFalse(result["installed_state_verified"])
        self.assertFalse(result["hardware_qualified"])
        self.assertFalse(result["attestation"])

    def test_failing_command_is_preserved(self):
        self.assertEqual(self.check(23).returncode, 23)
        result = self.create()
        self.assertFalse(result["recorded_checks_passed"])
        self.assertEqual(result["checks"][0]["exit_code"], 23)

    def test_changed_or_missing_log_cannot_pass(self):
        self.assertEqual(self.check().returncode, 0)
        self.log.write_text("replacement", encoding="utf-8")
        self.assertFalse(self.create()["recorded_checks_passed"])
        self.log.unlink()
        self.assertFalse(self.create()["recorded_checks_passed"])

    def test_missing_check_and_artifact_cannot_pass(self):
        result = self.create(artifacts=[self.out / "missing.dll"])
        self.assertFalse(result["recorded_checks_passed"])
        self.assertTrue(any("Missing executed-check" in p for p in result["problems"]))
        self.assertTrue(any("Missing artifact" in p for p in result["problems"]))

    def test_staged_unstaged_and_untracked_source_change_identity(self):
        original = receipt.snapshot(self.root)
        (self.root / "source.txt").write_text("changed\n", encoding="utf-8")
        unstaged = receipt.snapshot(self.root)
        self.assertNotEqual(original["snapshot_sha256"], unstaged["snapshot_sha256"])
        self.assertTrue(unstaged["dirty"])
        self.git("add", "source.txt")
        staged = receipt.snapshot(self.root)
        self.assertNotEqual(staged["snapshot_sha256"], unstaged["snapshot_sha256"])
        untracked = self.root / "new-source.txt"
        untracked.write_text("one", encoding="utf-8")
        first = receipt.snapshot(self.root)
        untracked.write_text("two", encoding="utf-8")
        self.assertNotEqual(first["snapshot_sha256"], receipt.snapshot(self.root)["snapshot_sha256"])

    def test_build_time_source_change_fails_but_ignored_output_does_not(self):
        self.assertEqual(self.check().returncode, 0)
        (self.out / "new-generated-file").write_text("output", encoding="utf-8")
        self.assertTrue(self.create()["recorded_checks_passed"])
        (self.root / "source.txt").unlink()
        self.assertFalse(self.create()["recorded_checks_passed"])

    def test_check_from_different_source_cannot_be_reused_after_restore(self):
        source = self.root / "source.txt"
        source.write_text("other candidate\n", encoding="utf-8")
        self.assertEqual(self.check().returncode, 0)
        source.write_text("baseline\n", encoding="utf-8")
        result = self.create()
        self.assertFalse(result["recorded_checks_passed"])
        self.assertTrue(any("Check source does not match" in p for p in result["problems"]))


if __name__ == "__main__":
    unittest.main()
