//! `wol wake`: magic packets to every interface of the selected machines.

use std::fmt::Write as _;
use std::net::SocketAddrV4;

use serde_json::json;

use crate::cli::Outcome;
use crate::config::ConfigFile;
use crate::error::{EXIT_NETWORK, Result};
use crate::magic;

/// Sends magic packets to every interface of the named machines and of every machine of the
/// named groups.
///
/// # Errors
///
/// Returns [`Error::MachineNotFound`] or [`Error::GroupNotFound`] before sending anything;
/// send failures are reported per machine, with exit code [`EXIT_NETWORK`].
pub fn wake(file: &ConfigFile, names: &[String], groups: &[String]) -> Result<Outcome> {
    let targets = file.config().select(names, groups)?;

    let mut results = Vec::new();
    let mut text = String::new();
    let mut exit = 0;
    for machine in targets {
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
            Ok(()) => {
                results.push(json!({ "machine": machine.name, "target": target.to_string(), "macs": macs, "status": "sent" }));
                let _ = writeln!(
                    text,
                    "Magic packet sent to \"{}\" ({target}, {}).",
                    machine.name,
                    macs.join(", ")
                );
            }
            Err(error) => {
                exit = EXIT_NETWORK;
                results.push(json!({
                    "machine": machine.name,
                    "target": target.to_string(),
                    "macs": macs,
                    "status": "failed",
                    "error": { "code": "network_error", "message": error.to_string() },
                }));
                eprintln!(
                    "error: cannot wake \"{}\" ({target}): {error}",
                    machine.name
                );
            }
        }
    }

    Ok(Outcome {
        json: json!({ "results": results }),
        text,
        exit,
    })
}
