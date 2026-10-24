"""Local nominal-rate AAudio queue estimates; never acoustic/end-to-end latency.

Input is one run folder. Timestamp age limit250ms, observation span limit5ms;
these are diagnostic rejection policies, not guarantees about the Android HAL.
The bracket range reflects sampling concurrency only; nominal-rate error and
opaque downstream DSP/speaker delay remain outside it. No Windows clock used.
"""
import collections
import json
from pathlib import Path
import re
import statistics
import sys

REQUIRED = ('frame monotonic_ns timestamp_result generation before_ns after_ns '
            'written_before written_after clock_result rate state_before state_after '
            'xruns buffer_frames capacity_frames device').split()


def estimate(s, previous=None):
    """Return (nominal lower/upper estimate in ms, reason); invalid stays None."""
    if any(k not in s for k in REQUIRED):
        return None, 'missing_fields'
    if s['clock_result'] != 0:
        return None, 'clock_unavailable'
    if s['timestamp_result'] != 0:
        return None, 'timestamp_unavailable'
    if s['state_before'] != 4 or s['state_after'] != 4:  # AAUDIO_STREAM_STATE_STARTED
        return None, 'not_started'
    if s['generation'] <= 0 or s['rate'] <= 0:
        return None, 'invalid_identity_or_rate'
    if s['before_ns'] <= 0 or s['after_ns'] < s['before_ns']:
        return None, 'invalid_clock_bracket'
    if s['after_ns'] - s['before_ns'] > 5_000_000:
        return None, 'observation_too_wide'
    if s['monotonic_ns'] <= 0 or s['monotonic_ns'] > s['before_ns']:
        return None, 'timestamp_future_or_invalid'
    if s['after_ns'] - s['monotonic_ns'] > 250_000_000:
        return None, 'timestamp_stale'
    if s['frame'] < 0 or s['written_before'] < 0 or s['written_after'] < s['written_before']:
        return None, 'invalid_frame_counters'
    if previous and s['generation'] == previous['generation']:
        if (s['rate'] != previous['rate'] or s['device'] != previous['device']):
            return None, 'stream_identity_changed'
        if (s['before_ns'] < previous['after_ns'] or
                s['frame'] < previous['frame'] or
                s['monotonic_ns'] < previous['monotonic_ns'] or
                s['written_before'] < previous['written_after']):
            return None, 'backward_observation'
    lower = ((s['written_before']-s['frame'])*1000/s['rate'] -
             (s['after_ns']-s['monotonic_ns'])/1_000_000)
    upper = ((s['written_after']-s['frame'])*1000/s['rate'] -
             (s['before_ns']-s['monotonic_ns'])/1_000_000)
    if not 0 <= lower <= upper <= 1000:
        return None, 'implausible_pending_estimate'
    return (lower, upper), None


def distribution(values):
    if not values:
        return None
    values = sorted(values)
    return {k: round(v, 3) for k, v in {
        'min': values[0], 'median': statistics.median(values),
        'p95': values[max(0, (95*len(values)+99)//100-1)], 'max': values[-1]
    }.items()}


def analyze(text):
    valid, rejected, previous = [], collections.Counter(), None
    ages, spans, sizes, xruns = [], [], set(), []
    for line in text.splitlines():
        if not line.startswith('TRACE Android OUTPUT_HAL '):
            continue
        s = {k: int(v) for k, v in re.findall(r'(\w+)=(-?\d+)', line)}
        value, reason = estimate(s, previous)
        if reason:
            rejected[reason] += 1
            continue
        previous = s  # Invalid data never advances the comparison frontier.
        valid.append(value)
        ages.append((s['after_ns']-s['monotonic_ns'])/1_000_000)
        spans.append((s['after_ns']-s['before_ns'])/1_000_000)
        sizes.add(s['buffer_frames'])
        if s['xruns'] >= 0:
            xruns.append(s['xruns'])
    return {'valid_samples': len(valid), 'rejected_samples': dict(rejected),
            'pending_nominal_lower_ms': distribution([v[0] for v in valid]),
            'pending_nominal_upper_ms': distribution([v[1] for v in valid]),
            'anchor_age_ms': distribution(ages), 'observation_span_ms': distribution(spans),
            'observed_buffer_frames': sorted(sizes),
            'known_xrun_samples': len(xruns),
            'scope': 'AAudio system presentation estimate at nominal rate; excludes source/network/native inventory and unreported downstream delay; not acoustic latency'}


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: python analyze-android-presentation.py RUN_FOLDER')
    path = Path(sys.argv[1])/'android-receiver.txt'
    print(json.dumps(analyze(path.read_text(encoding='utf-8-sig', errors='replace')), indent=2))
