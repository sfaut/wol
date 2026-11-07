//! Continuous monitoring (`wol watch`): one NDJSON event or one text line per state change.
//!
//! The configuration file is re-read whenever it changes on disk, so machines added or edited
//! (by hand, or by a front-end through other `wol` commands) are picked up without a restart.
//!
//! Machines set to wake automatically (`auto_wake`) are woken whenever they are found offline.

use std::collections::HashMap;
use std::fs;
use std::path::Path;
use std::time::{Duration, Instant, SystemTime};

use serde_json::{Value, json};

use crate::cli::{document, emit, emit_text, probe_json, probe_text, status_text};
use crate::config::{ConfigFile, Machine};
use crate::error::{Error, Result};
use crate::probe::{self, State};
use crate::{scan, time, wake};

/// Number of consecutive failed checks before an online machine is reported as not online,
/// so that a single missed answer does not trigger a notification.
const CONFIRMATIONS: u32 = 2;

/// Shortest time between two automatic wake-ups of a machine that stays offline.
const AUTO_WAKE_RETRY: Duration = Duration::from_secs(60);

/// Options of `wol watch`.
pub struct Options<'a> {
    /// Names of the machines to monitor.
    pub names: &'a [String],

    /// Groups whose machines are monitored.
    pub groups: &'a [String],

    /// Time between the starts of two checks.
    pub interval: Duration,

    /// Whether to print NDJSON instead of text.
    pub json: bool,
}

/// Last reported state of a machine.
struct Tracked {
    /// State last reported to the consumer.
    reported: State,

    /// Consecutive checks that disagreed with an `online` reported state.
    failures: u32,
}

impl Tracked {
    /// Records the state found by a check; returns the previous state when a change must be
    /// reported (immediately, except when leaving `online`, see [`CONFIRMATIONS`]).
    fn update(&mut self, state: State) -> Option<State> {
        if state == self.reported {
            self.failures = 0;
            return None;
        }
        if self.reported == State::Online {
            self.failures += 1;
            if self.failures < CONFIRMATIONS {
                return None;
            }
        }

        let previous = self.reported;
        self.reported = state;
        self.failures = 0;
        Some(previous)
    }
}

/// Automatic wake-ups of a machine since it was last online.
struct AutoWake {
    /// Number of times its magic packets were sent.
    attempts: u32,

    /// Start of the check that last sent them.
    last: Instant,
}

/// Automatic wake-ups of the auto-wake machines; kept across configuration reloads, so that a
/// reload neither resends the packets nor resets the count.
struct AutoWakes {
    /// Time between two wake-ups of a machine that stays offline (see [`retry_delay`]).
    retry: Duration,

    /// Wake-ups by lowercase machine name.
    machines: HashMap<String, AutoWake>,
}

impl AutoWakes {
    /// Starts with no machine woken, for checks `interval` apart.
    fn new(interval: Duration) -> Self {
        Self {
            retry: retry_delay(interval),
            machines: HashMap::new(),
        }
    }

    /// Records the reported state of a machine at the check started at `now`; returns the
    /// attempt number when its magic packets must be sent: at once when it is found offline,
    /// then every `retry` while it stays offline. Being online, or no longer set to wake
    /// automatically, starts the count again.
    fn due(&mut self, key: &str, auto_wake: bool, state: State, now: Instant) -> Option<u32> {
        if !auto_wake || state == State::Online {
            self.machines.remove(key);
            return None;
        }
        if state != State::Offline {
            return None;
        }

        match self.machines.get_mut(key) {
            Some(wake) if now.duration_since(wake.last) < self.retry => None,
            Some(wake) => {
                wake.attempts += 1;
                wake.last = now;
                Some(wake.attempts)
            }
            None => {
                self.machines.insert(
                    key.to_owned(),
                    AutoWake {
                        attempts: 1,
                        last: now,
                    },
                );
                Some(1)
            }
        }
    }

    /// Forgets the machines that are no longer watched (removed, renamed or deselected).
    fn retain(&mut self, keys: &[String]) {
        self.machines.retain(|key, _| keys.contains(key));
    }
}

/// Time between two automatic wake-ups of a machine that stays offline, for checks `interval`
/// apart: wake-ups happen at checks, so the first check at least [`AUTO_WAKE_RETRY`] after the
/// previous one sends the packets again (checks start `interval` apart, or later when a scan
/// takes longer).
fn retry_delay(interval: Duration) -> Duration {
    let interval = interval.as_secs().max(1);
    Duration::from_secs(AUTO_WAKE_RETRY.as_secs().div_ceil(interval) * interval)
}

/// Monitors the selected machines until the process is stopped.
///
/// # Errors
///
/// Returns [`Error::MachineNotFound`] or [`Error::GroupNotFound`] when the initial selection
/// is invalid; later errors (invalid edited file) are reported as `error` events.
pub fn watch(file: &mut ConfigFile, options: &Options<'_>) -> Result<crate::cli::Outcome> {
    file.config().select(options.names, options.groups)?;

    let mut modified = modified_time(file.path());
    let mut tracked: HashMap<String, Tracked> = HashMap::new();
    let mut auto_wakes = AutoWakes::new(options.interval);
    let mut snapshot = true;

    loop {
        let started = Instant::now();

        let current = modified_time(file.path());
        if current != modified {
            modified = current;
            match ConfigFile::load(file.path().to_owned()) {
                Ok(reloaded) => match reloaded.config().select(options.names, options.groups) {
                    Ok(_) => {
                        *file = reloaded;
                        tracked.clear();
                        snapshot = true;
                    }
                    Err(error) => report(options, &error),
                },
                Err(error) => report(options, &error),
            }
        }

        let machines = file
            .config()
            .select(options.names, options.groups)
            .unwrap_or_default();
        let probes = probe::probe_all(&machines, &scan::scan());
        let checked_at = time::now();

        if snapshot {
            snapshot = false;
            for (machine, probe) in machines.iter().zip(&probes) {
                tracked.insert(
                    machine.name.to_lowercase(),
                    Tracked {
                        reported: probe.state,
                        failures: 0,
                    },
                );
            }

            if options.json {
                let entries: Vec<Value> = machines
                    .iter()
                    .zip(&probes)
                    .map(|(machine, probe)| probe_json(machine, probe))
                    .collect();
                emit(&document(
                    None,
                    json!({
                        "event": "snapshot",
                        "config": { "path": file.path(), "status": file.status() },
                        "checked_at": checked_at,
                        "interval_s": options.interval.as_secs(),
                        "auto_wake_retry_s": auto_wakes.retry.as_secs(),
                        "machines": entries,
                    }),
                ));
            } else {
                emit_text(&format!(
                    "Watching {} machine(s) every {} s (Ctrl+C to stop).\nConfiguration: {}\n{}",
                    machines.len(),
                    options.interval.as_secs(),
                    file.path().display(),
                    status_text(file.config(), &machines, &probes).trim_end(),
                ));
            }
        } else {
            for (machine, probe) in machines.iter().zip(&probes) {
                let entry = tracked
                    .entry(machine.name.to_lowercase())
                    .or_insert(Tracked {
                        reported: probe.state,
                        failures: 0,
                    });
                let Some(previous) = entry.update(probe.state) else {
                    continue;
                };

                if options.json {
                    emit(&document(
                        None,
                        json!({
                            "event": "state_changed",
                            "checked_at": checked_at,
                            "previous": previous,
                            "machine": probe_json(machine, probe),
                        }),
                    ));
                } else {
                    emit_text(&format!(
                        "{}  {}: {} -> {}",
                        utc_text(&checked_at),
                        machine.name,
                        previous.code(),
                        probe_text(probe)
                    ));
                }
            }
        }

        let keys: Vec<String> = machines
            .iter()
            .map(|machine| machine.name.to_lowercase())
            .collect();
        auto_wakes.retain(&keys);
        for (machine, key) in machines.iter().zip(&keys) {
            let Some(state) = tracked.get(key).map(|entry| entry.reported) else {
                continue;
            };
            if let Some(attempt) = auto_wakes.due(key, machine.auto_wake, state, started) {
                auto_wake(machine, attempt, &checked_at, options.json);
            }
        }

        std::thread::sleep(options.interval.saturating_sub(started.elapsed()));
    }
}

/// Sends the magic packets of an auto-wake machine found offline and reports it: `auto_wake`
/// event in JSON, otherwise one line (on `stderr` when sending failed).
fn auto_wake(machine: &Machine, attempt: u32, checked_at: &str, json: bool) {
    let (result, outcome) = wake::send(machine);
    if json {
        emit(&document(
            None,
            json!({
                "event": "auto_wake",
                "checked_at": checked_at,
                "attempt": attempt,
                "result": result,
            }),
        ));
        return;
    }

    match outcome {
        Ok(_) => emit_text(&format!(
            "{}  {}: offline, magic packet sent automatically ({}, attempt {attempt})",
            utc_text(checked_at),
            machine.name,
            wake::target(machine),
        )),
        Err(line) => eprint!("{line}"),
    }
}

/// Formats an RFC 3339 timestamp for humans, e.g. `2026-10-18 12:37:23 UTC`.
///
/// Human output shows UTC: local time zones need a date library.
fn utc_text(timestamp: &str) -> String {
    format!(
        "{} UTC",
        timestamp.get(..19).unwrap_or(timestamp).replace('T', " ")
    )
}

/// Last modification time of the configuration file (`None` when it does not exist).
fn modified_time(path: &Path) -> Option<SystemTime> {
    fs::metadata(path)
        .and_then(|metadata| metadata.modified())
        .ok()
}

/// Reports an error without stopping: `error` event in JSON, `error:` line on stderr otherwise.
fn report(options: &Options<'_>, error: &Error) {
    if options.json {
        emit(&document(
            None,
            json!({
                "event": "error",
                "error": { "code": error.code(), "message": error.to_string(), "details": error.details() },
            }),
        ));
    } else {
        eprintln!("error: {error}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Feeds successive check results and returns the reported changes, as (previous, new).
    fn changes(initial: State, checks: &[State]) -> Vec<(State, State)> {
        let mut tracked = Tracked {
            reported: initial,
            failures: 0,
        };
        checks
            .iter()
            .filter_map(|&state| tracked.update(state).map(|previous| (previous, state)))
            .collect()
    }

    #[test]
    fn coming_online_is_reported_immediately() {
        assert_eq!(
            changes(State::Offline, &[State::Online]),
            [(State::Offline, State::Online)]
        );
    }

    #[test]
    fn going_offline_needs_confirmation() {
        assert_eq!(changes(State::Online, &[State::Offline]), []);
        assert_eq!(
            changes(State::Online, &[State::Offline, State::Offline]),
            [(State::Online, State::Offline)]
        );
    }

    #[test]
    fn single_lost_check_is_ignored() {
        assert_eq!(
            changes(
                State::Online,
                &[State::Offline, State::Online, State::Offline, State::Online]
            ),
            []
        );
    }

    #[test]
    fn auto_wake_sends_at_once_then_every_retry_period() {
        let mut wakes = AutoWakes::new(Duration::from_secs(10));
        let start = Instant::now();
        let at = |seconds| start + Duration::from_secs(seconds);

        assert_eq!(wakes.due("nas", true, State::Offline, at(0)), Some(1));
        assert_eq!(wakes.due("nas", true, State::Offline, at(10)), None);
        assert_eq!(wakes.due("nas", true, State::Offline, at(59)), None);
        assert_eq!(wakes.due("nas", true, State::Offline, at(60)), Some(2));
        assert_eq!(wakes.due("nas", true, State::Offline, at(120)), Some(3));
    }

    #[test]
    fn retry_delay_is_a_whole_number_of_checks() {
        let delay = |seconds| retry_delay(Duration::from_secs(seconds)).as_secs();

        assert_eq!(delay(1), 60);
        assert_eq!(delay(10), 60);
        assert_eq!(delay(7), 63);
        assert_eq!(delay(45), 90);
        assert_eq!(delay(60), 60);
        assert_eq!(delay(300), 300);
    }

    #[test]
    fn auto_wake_starts_again_after_being_online() {
        let mut wakes = AutoWakes::new(Duration::from_secs(10));
        let start = Instant::now();
        let at = |seconds| start + Duration::from_secs(seconds);

        assert_eq!(wakes.due("nas", true, State::Offline, at(0)), Some(1));
        assert_eq!(wakes.due("nas", true, State::Online, at(30)), None);
        assert_eq!(wakes.due("nas", true, State::Offline, at(40)), Some(1));
    }

    #[test]
    fn auto_wake_skips_errors_and_other_machines() {
        let mut wakes = AutoWakes::new(Duration::from_secs(10));
        let start = Instant::now();
        let at = |seconds| start + Duration::from_secs(seconds);

        assert_eq!(wakes.due("pc", false, State::Offline, at(0)), None);
        assert_eq!(wakes.due("nas", true, State::Error, at(0)), None);
        assert_eq!(wakes.due("nas", true, State::Offline, at(10)), Some(1));
        // The count goes on through errors, and the retry period still applies.
        assert_eq!(wakes.due("nas", true, State::Error, at(20)), None);
        assert_eq!(wakes.due("nas", true, State::Offline, at(30)), None);
        assert_eq!(wakes.due("nas", true, State::Offline, at(70)), Some(2));
        // Turning the option off starts the count again.
        assert_eq!(wakes.due("nas", false, State::Offline, at(140)), None);
        assert_eq!(wakes.due("nas", true, State::Offline, at(150)), Some(1));
    }

    #[test]
    fn other_changes_are_immediate() {
        assert_eq!(
            changes(State::Offline, &[State::Error, State::Offline]),
            [
                (State::Offline, State::Error),
                (State::Error, State::Offline)
            ]
        );
    }
}
