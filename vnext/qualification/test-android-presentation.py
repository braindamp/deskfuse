"""Protect measured-delay reporting against false low-latency claims."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('presentation', Path(__file__).with_name('analyze-android-presentation.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


def sample(**changes):
    s = dict(frame=48000, monotonic_ns=1_000_000_000, timestamp_result=0,
             generation=1, before_ns=1_010_000_000, after_ns=1_010_100_000,
             written_before=53280, written_after=53280, clock_result=0,
             rate=48000, state_before=4, state_after=4, xruns=0,
             buffer_frames=4104, capacity_frames=4104, device=2)
    return s | changes


class MeasurementTests(unittest.TestCase):
    def test_known_offset_and_concurrent_callback_envelope(self):
        self.assertEqual(m.estimate(sample()), ((99.9, 100.0), None))
        self.assertEqual(m.estimate(sample(written_after=53760)), ((99.9, 110.0), None))

    def test_failures_do_not_become_zero_latency(self):
        for changes in [dict(timestamp_result=-899), dict(clock_result=-1),
                        dict(state_before=3), dict(generation=0), dict(rate=0),
                        dict(frame=-1), dict(written_after=53000)]:
            self.assertIsNone(m.estimate(sample(**changes))[0])

    def test_stale_future_and_wide_brackets_rejected(self):
        for changes in [dict(monotonic_ns=700_000_000), dict(monotonic_ns=1_011_000_000),
                        dict(after_ns=1_016_000_000), dict(after_ns=1_000_000_000)]:
            self.assertIsNone(m.estimate(sample(**changes))[0])

    def test_negative_or_extreme_pending_not_clamped(self):
        self.assertIsNone(m.estimate(sample(written_before=48000, written_after=48000))[0])
        self.assertIsNone(m.estimate(sample(written_before=100000, written_after=100000))[0])

    def test_backward_same_stream_rejected_new_stream_resets(self):
        previous = sample()
        for changes in [dict(frame=47999), dict(written_before=53279),
                        dict(monotonic_ns=999_999_999), dict(device=3), dict(rate=44100)]:
            self.assertIsNone(m.estimate(sample(before_ns=1_011_000_000,
                                              after_ns=1_011_100_000, **changes), previous)[0])
        self.assertIsNotNone(m.estimate(sample(generation=2), previous)[0])

    def test_legacy_logs_and_unknown_xruns_explicit(self):
        row = 'TRACE Android OUTPUT_HAL ' + ' '.join(f'{k}={v}' for k, v in sample(xruns=-1).items())
        report = m.analyze(row+'\nTRACE Android OUTPUT_HAL frame=48000 monotonic_ns=1 timestamp_result=0')
        self.assertEqual(report['valid_samples'], 1)
        self.assertEqual(report['known_xrun_samples'], 0)
        self.assertEqual(report['rejected_samples'], {'missing_fields': 1})


if __name__ == '__main__':
    unittest.main()
