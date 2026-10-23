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
    let mut counter = 0i64;
    let mut frequency = 0i64;
    let ok = unsafe {
        QueryPerformanceFrequency(&mut frequency) != 0
            && QueryPerformanceCounter(&mut counter) != 0
    };
    if !ok || frequency <= 0 || counter < 0 {
        return Err(io::Error::new(io::ErrorKind::Other, "Windows QPC unavailable"));
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
    fn no_silent_wraparound() {
        assert_eq!(classify(10, u64::MAX), AgeDecision::FutureTimestamp);
        assert_eq!(classify(u64::MAX, 1), AgeDecision::Expired);
    }
}
