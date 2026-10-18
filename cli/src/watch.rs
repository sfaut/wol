//! Continuous monitoring (`wol watch`): one NDJSON event or one text line per state change.
//!
//! The configuration file is re-read whenever it changes on disk, so machines added or edited
//! (by hand, or by a front-end through other `wol` commands) are picked up without a restart.

use std::collections::HashMap;
use std::fs;
use std::io::{self, Write as _};
use std::path::Path;
use std::time::{Duration, Instant, SystemTime};

use serde_json::{Value, json};

use crate::cli::{document, probe_json, probe_text, status_text};
use crate::config::ConfigFile;
use crate::error::{Error, Result};
use crate::probe::{self, State};
use crate::time;

/// Number of consecutive failed checks before an online machine is reported as not online,
/// so that a single lost ping does not trigger a notification.
const CONFIRMATIONS: u32 = 2;

/// Options of `wol watch`.
pub struct Options<'a> {
    /// Names of the machines to monitor.
    pub names: &'a [String],

    /// Groups whose machines are monitored.
    pub groups: &'a [String],

    /// Time between the starts of two checks.
    pub interval: Duration,

    /// Time to wait for each answer.
    pub timeout: Duration,

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
        let probes = probe::probe_all(&machines, options.timeout);
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
                    // Human output shows UTC: local time zones need a date library.
                    let at = checked_at
                        .get(..19)
                        .unwrap_or(&checked_at)
                        .replace('T', " ");
                    emit_text(&format!(
                        "{at} UTC  {}: {} -> {}",
                        machine.name,
                        previous.code(),
                        probe_text(probe)
                    ));
                }
            }
        }

        std::thread::sleep(options.interval.saturating_sub(started.elapsed()));
    }
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

/// Prints one NDJSON line.
fn emit(event: &Value) {
    emit_text(&event.to_string());
}

/// Prints a line and flushes it; exits quietly when the consumer has gone (closed pipe).
fn emit_text(line: &str) {
    let mut stdout = io::stdout().lock();
    if writeln!(stdout, "{line}")
        .and_then(|()| stdout.flush())
        .is_err()
    {
        std::process::exit(0);
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
    fn other_changes_are_immediate() {
        assert_eq!(
            changes(State::Offline, &[State::Error, State::Unknown]),
            [
                (State::Offline, State::Error),
                (State::Error, State::Unknown)
            ]
        );
    }
}
