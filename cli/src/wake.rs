//! `wol wake`: magic packets to every interface of the selected machines, then, with `--wait`,
//! local network scans until the machines come online or the time is up.
//!
//! Without `--wait`, the command prints one document. With it, it streams what happens: one
//! text line, or one NDJSON event, per step.

use std::net::SocketAddrV4;
use std::time::{Duration, Instant};

use serde_json::{Value, json};

use crate::cli::{Outcome, document, emit, emit_text, probe_json};
use crate::config::{ConfigFile, Machine};
use crate::error::{EXIT_NETWORK, EXIT_TIMEOUT, Error, Result};
use crate::probe::{self, State};
use crate::{magic, scan};

/// Shortest time between the starts of two scans while waiting.
const SCAN_PERIOD: Duration = Duration::from_secs(2);

/// Options of `wol wake`.
pub struct Options<'a> {
    /// Names of the machines to wake.
    pub names: &'a [String],

    /// Groups whose machines are woken.
    pub groups: &'a [String],

    /// How long to wait for the machines to come online; no waiting when absent.
    pub wait: Option<Duration>,

    /// Whether to print JSON instead of text.
    pub json: bool,
}

/// Sends magic packets to every interface of the named machines and of every machine of the
/// named groups, then waits for them when asked to.
///
/// # Errors
///
/// Returns [`Error::MachineNotFound`] or [`Error::GroupNotFound`] before sending anything;
/// send failures are reported per machine, with exit code [`EXIT_NETWORK`]. Machines still
/// offline when the wait is over give exit code [`EXIT_TIMEOUT`].
pub fn wake(file: &ConfigFile, options: &Options<'_>) -> Result<Outcome> {
    let targets = file.config().select(options.names, options.groups)?;

    let mut results = Vec::new();
    let mut text = String::new();
    let mut sent = Vec::new();
    let mut failed = Vec::new();
    for machine in targets {
        let (result, outcome) = send(machine);
        results.push(result);
        match outcome {
            Ok(line) => {
                text.push_str(&line);
                sent.push(machine);
            }
            Err(line) => {
                eprint!("{line}");
                failed.push(machine.name.clone());
            }
        }
    }
    let exit = if failed.is_empty() { 0 } else { EXIT_NETWORK };

    let Some(timeout) = options.wait else {
        return Ok(Outcome {
            json: json!({ "results": results }),
            text,
            exit,
            streamed: false,
        });
    };

    if options.json {
        emit(&document(
            None,
            json!({
                "event": "sent",
                "config": { "path": file.path(), "status": file.status() },
                "timeout_s": timeout.as_secs(),
                "results": results,
            }),
        ));
    } else {
        if !text.is_empty() {
            emit_text(text.trim_end());
        }
        if !sent.is_empty() {
            emit_text(&format!(
                "Waiting up to {} s for {} machine(s) to come online (Ctrl+C to stop).",
                timeout.as_secs(),
                sent.len()
            ));
        }
    }

    let (online, timed_out) = wait(&sent, timeout, options.json);
    if options.json {
        emit(&document(
            None,
            json!({
                "event": "finished",
                "online": online,
                "timed_out": timed_out,
                "failed": failed,
            }),
        ));
    }

    Ok(Outcome {
        json: Value::Null,
        text: String::new(),
        exit: match (exit, timed_out.is_empty()) {
            (0, false) => EXIT_TIMEOUT,
            (exit, _) => exit,
        },
        streamed: true,
    })
}

/// Sends magic packets to every interface of a machine.
///
/// Returns the JSON result, and the text line to print: on `stdout` when sent (`Ok`), on
/// `stderr` when failed (`Err`).
fn send(machine: &Machine) -> (Value, std::result::Result<String, String>) {
    let target = SocketAddrV4::new(
        machine.broadcast.unwrap_or(magic::DEFAULT_BROADCAST),
        machine.port.unwrap_or(magic::DEFAULT_PORT),
    );
    let macs: Vec<String> = machine
        .interfaces
        .iter()
        .map(|interface| interface.mac.to_string())
        .collect();
    let sent = machine
        .interfaces
        .iter()
        .try_for_each(|interface| magic::send(interface.mac, target));

    match sent {
        Ok(()) => (
            json!({ "machine": machine.name, "target": target.to_string(), "macs": macs, "status": "sent" }),
            Ok(format!(
                "Magic packet sent to \"{}\" ({target}, {}).\n",
                machine.name,
                macs.join(", ")
            )),
        ),
        Err(error) => (
            json!({
                "machine": machine.name,
                "target": target.to_string(),
                "macs": macs,
                "status": "failed",
                "error": { "code": "network_error", "message": error.to_string() },
            }),
            Err(format!(
                "error: cannot wake \"{}\" ({target}): {error}\n",
                machine.name
            )),
        ),
    }
}

/// Scans the local network until every machine is online or `timeout` is over, reporting
/// each machine as it comes online, then the ones that did not.
///
/// A failing scan (no local network) is reported once, and scans go on until the time is up.
///
/// Returns the names of the machines that came online, then of those that timed out.
fn wait(machines: &[&Machine], timeout: Duration, json: bool) -> (Vec<String>, Vec<String>) {
    let started = Instant::now();
    let mut pending: Vec<&Machine> = machines.to_vec();
    let mut online = Vec::new();
    let mut scan_failing = false;

    while !pending.is_empty() {
        let round = Instant::now();
        let scan = scan::scan();
        match &scan {
            Err(error) if !scan_failing => {
                scan_failing = true;
                let error = Error::Scan((*error).into());
                if json {
                    emit(&document(
                        None,
                        json!({
                            "event": "error",
                            "error": { "code": error.code(), "message": error.to_string(), "details": error.details() },
                        }),
                    ));
                } else {
                    eprintln!("error: {error}; still trying");
                }
            }
            Err(_) => {}
            Ok(_) => scan_failing = false,
        }

        let probes = probe::probe_all(&pending, &scan);
        let elapsed = started.elapsed().as_secs();
        let mut still_offline = Vec::new();
        for (machine, probe) in pending.into_iter().zip(&probes) {
            if probe.state != State::Online {
                still_offline.push(machine);
                continue;
            }
            online.push(machine.name.clone());
            if json {
                emit(&document(
                    None,
                    json!({ "event": "online", "elapsed_s": elapsed, "machine": probe_json(machine, probe) }),
                ));
            } else {
                let address = probe.address.map(|address| address.to_string());
                let label = probe
                    .interface
                    .as_ref()
                    .and_then(|interface| interface.label.clone());
                let details: Vec<String> = address.into_iter().chain(label).collect();
                emit_text(&format!(
                    "\"{}\" is online after {elapsed} s ({}).",
                    machine.name,
                    details.join(", ")
                ));
            }
        }
        pending = still_offline;

        let remaining = timeout.saturating_sub(started.elapsed());
        if pending.is_empty() || remaining.is_zero() {
            break;
        }
        std::thread::sleep(SCAN_PERIOD.saturating_sub(round.elapsed()).min(remaining));
    }

    let timed_out: Vec<String> = pending.iter().map(|machine| machine.name.clone()).collect();
    for name in &timed_out {
        if json {
            emit(&document(
                None,
                json!({ "event": "timed_out", "elapsed_s": timeout.as_secs(), "machine": name }),
            ));
        } else {
            emit_text(&format!(
                "\"{name}\" did not come online within {} s.",
                timeout.as_secs()
            ));
        }
    }

    (online, timed_out)
}
