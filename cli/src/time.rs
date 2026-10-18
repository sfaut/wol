//! Timestamps for JSON output (RFC 3339, UTC), without a date library.

use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Returns the current time as an RFC 3339 UTC timestamp with milliseconds.
pub fn now() -> String {
    format_rfc3339(
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default(),
    )
}

/// Formats a duration since the Unix epoch as `YYYY-MM-DDTHH:MM:SS.mmmZ`.
pub fn format_rfc3339(since_epoch: Duration) -> String {
    let seconds = since_epoch.as_secs();
    let (year, month, day) = civil_from_days((seconds / 86_400) as i64);
    let time = seconds % 86_400;
    let (hour, minute, second) = (time / 3_600, time % 3_600 / 60, time % 60);
    let millis = since_epoch.subsec_millis();
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

/// Converts a number of days since 1970-01-01 to a (year, month, day) date of the proleptic
/// Gregorian calendar (Howard Hinnant's `civil_from_days` algorithm).
fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let shifted = days + 719_468;
    let era = shifted.div_euclid(146_097);
    let day_of_era = shifted.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = (day_of_year - (153 * month_index + 2) / 5 + 1) as u32;
    let month = if month_index < 10 {
        month_index + 3
    } else {
        month_index - 9
    } as u32;
    let year = year_of_era + era * 400 + i64::from(month <= 2);
    (year, month, day)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_known_instants() {
        assert_eq!(format_rfc3339(Duration::ZERO), "1970-01-01T00:00:00.000Z");
        assert_eq!(
            format_rfc3339(Duration::from_millis(1_700_000_000_123)),
            "2023-11-14T22:13:20.123Z"
        );
        assert_eq!(
            format_rfc3339(Duration::from_secs(951_782_400)),
            "2000-02-29T00:00:00.000Z"
        );
        assert_eq!(
            format_rfc3339(Duration::from_secs(4_102_444_799)),
            "2099-12-31T23:59:59.000Z"
        );
    }
}
