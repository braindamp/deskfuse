//! CP-001 real Windows capture age bound. A WASAPI source timestamp is
//! in QPC-derived 100-ns units; the sender must use the SAME local QPC
//! domain rather than merely measuring residence after pipe read.
//! This qualifies age at plaintext offer, NOT sealed QUIC packet age.
use std::io;
use std::time::Duration;

pub const MAX_SOURCE_AGE_100NS: u64 = 600_000; // 60 ms upper bound
pub const MAX_FUTURE_SKEW_100NS: u64 = 100_000; // 10 ms, report separately

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AgeDecision {
    Fresh { age_100ns: u64 },
    MissingTimestamp,
    FutureTimestamp,
    Expired,
}

/// Recover the first-frame QPC of the original native GetBuffer packet.
/// Fragment provenance changes neither the source timestamp nor its age.
pub fn original_packet_qpc_100ns(source_100ns: u64, offset_frames: u32) -> Option<u64> {
    if source_100ns == 0 { return None; }
    source_100ns.checked_sub(u64::from(offset_frames) * 10_000_000 / 48_000)
        .filter(|qpc| *qpc != 0)
}
pub fn classify(now_100ns: u64, source_100ns: u64) -> AgeDecision {
    if source_100ns == 0 { return AgeDecision::MissingTimestamp; }
    if source_100ns > now_100ns {
        let future = source_100ns - now_100ns;
        if future > MAX_FUTURE_SKEW_100NS { return AgeDecision::FutureTimestamp; }
        // Accept small clock/reporting uncertainty without inventing negative age.
        return AgeDecision::Fresh { age_100ns: 0 };
    }
    let age = now_100ns - source_100ns;
    if age > MAX_SOURCE_AGE_100NS { AgeDecision::Expired }
    else { AgeDecision::Fresh { age_100ns: age } }
}
/// Qualify already-delivered WASAPI loopback PCM using the original
/// GetBuffer packet's first-frame QPC when its native 240-frame splitting
/// advances a *derived* timestamp beyond the normal future-skew cutoff.
///
/// Only the second 5ms fragment of a real original packet may use the
/// exception. The original first-frame QPC must still satisfy the exact
/// existing 10ms future bound. The fragment's own timestamp still controls
/// the unchanged 60ms stale-source bound. No normalization, waiting,
/// synthetic pacing or expanded packet queue is permitted.
pub fn classify_wasapi(
    now_100ns: u64, source_100ns: u64, original_offset_frames: u32
) -> AgeDecision {
    let ordinary = classify(now_100ns, source_100ns);
    if ordinary != AgeDecision::FutureTimestamp {
        return ordinary; // In particular, never relax Missing or Expired.
    }
    if original_offset_frames == 0 || original_offset_frames > 240 {
        return ordinary;
    }
    let Some(original_first_qpc) = original_packet_qpc_100ns(source_100ns, original_offset_frames) else {
        return AgeDecision::MissingTimestamp;
    };
    // A fragment is eligible only if the FIRST frame of the same original
    // packet would have passed the unmodified QPC classifier at this moment.
    // Thus the largest newly eligible lead is 15ms, not an unlimited future.
    if matches!(classify(now_100ns, original_first_qpc),
                AgeDecision::Fresh { .. }) {
        AgeDecision::Fresh { age_100ns: 0 }
    } else {
        ordinary
    }
}

pub fn max_source_age() -> Duration {
    Duration::from_nanos(MAX_SOURCE_AGE_100NS * 100)
}

#[cfg(windows)]
pub fn local_qpc_100ns() -> io::Result<u64> {
    #[link(name = "Kernel32")]
    unsafe extern "system" {
        fn QueryPerformanceCounter(counter: *mut i64) -> i32;
        fn QueryPerformanceFrequency(frequency: *mut i64) -> i32;
    }
    // Windows QPC frequency is fixed for the running system. Query it once,
    // not on every WASAPI block / PCM admission freshness check. QPC counter
    // is still sampled freshly for each decision; no cached age or new clock.
    static FREQUENCY: std::sync::OnceLock<i64> = std::sync::OnceLock::new();
    let frequency = *FREQUENCY.get_or_init(|| {
        let mut value = 0i64;
        if unsafe { QueryPerformanceFrequency(&mut value) } == 0 { 0 } else { value }
    });
    if frequency <= 0 {
        return Err(io::Error::new(io::ErrorKind::Other, "Windows QPC frequency unavailable"));
    }
    let mut counter = 0i64;
    if unsafe { QueryPerformanceCounter(&mut counter) } == 0 || counter < 0 {
        return Err(io::Error::new(io::ErrorKind::Other, "Windows QPC counter unavailable"));
    }
    let ticks = (counter as u128) * 10_000_000u128 / (frequency as u128);
    u64::try_from(ticks).map_err(|_| {
        io::Error::new(io::ErrorKind::InvalidData, "Windows QPC 100ns overflow")
    })
}

#[cfg(not(windows))]
pub fn local_qpc_100ns() -> io::Result<u64> {
    Err(io::Error::new(io::ErrorKind::Unsupported,
        "Windows QPC capture age requires Windows host"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn real_age_decisions_are_fail_closed_for_missing_and_old() {
        assert_eq!(classify(2_000_000, 0), AgeDecision::MissingTimestamp);
        assert_eq!(classify(2_000_000, 1_300_000), AgeDecision::Expired);
        assert_eq!(classify(2_000_000, 1_400_000),
                   AgeDecision::Fresh { age_100ns: 600_000 });
        assert_eq!(classify(2_000_000, 1_999_999),
                   AgeDecision::Fresh { age_100ns: 1 });
        assert_eq!(classify(2_000_000, 2_050_000),
                   AgeDecision::Fresh { age_100ns: 0 });
        assert_eq!(classify(2_000_000, 2_100_001),
                   AgeDecision::FutureTimestamp);
        assert_eq!(max_source_age(), Duration::from_millis(60));
    }
    #[test]
    fn two_240_frame_halves_can_straddle_existing_future_cutoff() {
        // Reproduces the physical Windows failure hypothesis without
        // changing any production eligibility, source clock or queue bounds.
        // Both samples are associated with ONE synthetic original WASAPI
        // GetBuffer packet; its second 240-frame half is 5 ms later.
        let now = 2_000_000u64;
        let first = now + 95_650; // +9.565 ms: currently admitted
        let second = first + 240 * 10_000_000 / 48_000; // +14.565 ms
        assert_eq!(second - first, 50_000);
        assert_eq!(classify(now, first), AgeDecision::Fresh { age_100ns: 0 });
        assert_eq!(classify(now, second), AgeDecision::FutureTimestamp);
        // A different QPC observation for the second half is possible,
        // but ordinary ~0.5-ms processing cannot bridge this cutoff.
        assert_eq!(classify(now + 5_000, second), AgeDecision::FutureTimestamp);
        // Preserve the hard 60-ms past-age limit during this diagnosis.
        assert_eq!(classify(now, now - MAX_SOURCE_AGE_100NS - 1),
                   AgeDecision::Expired);
        assert_eq!(classify(now, 0), AgeDecision::MissingTimestamp);
    }
    #[test]
    fn original_getbuffer_240_frame_halves_pass_without_erasing_source_time() {
        let now = 2_000_000u64;
        let first = now + 95_650;
        let second = first + 50_000;
        assert_eq!(classify_wasapi(now, first, 0),
                   AgeDecision::Fresh { age_100ns: 0 });
        assert_eq!(classify(now, second), AgeDecision::FutureTimestamp);
        assert_eq!(classify_wasapi(now, second, 240),
                   AgeDecision::Fresh { age_100ns: 0 });
        // The same timestamp without verified native packet membership
        // still fails closed; this is not a blanket 15ms skew increase.
        assert_eq!(classify_wasapi(now, second, 0), AgeDecision::FutureTimestamp);
        assert_eq!(classify_wasapi(now, second, 480), AgeDecision::FutureTimestamp);
        // A first packet timestamp that is truly too far ahead cannot
        // bootstrap the exception for its later 5ms fragment.
        assert_eq!(classify_wasapi(now, now + 165_000, 240),
                   AgeDecision::FutureTimestamp);
        assert_eq!(classify_wasapi(now, now + 150_001, 240),
                   AgeDecision::FutureTimestamp);
        // Past-age protection applies to the actual first PCM frame.
        assert_eq!(classify_wasapi(now, now - 600_001, 240),
                   AgeDecision::Expired);
        assert_eq!(classify_wasapi(now, 0, 240),
                   AgeDecision::MissingTimestamp);
        assert_eq!(classify_wasapi(now, 1, 240),
                   AgeDecision::Expired); // old audio is never accepted.
    }
    #[test]
    fn no_silent_wraparound() {
        assert_eq!(classify(10, u64::MAX), AgeDecision::FutureTimestamp);
        assert_eq!(classify(u64::MAX, 1), AgeDecision::Expired);
    }
}
