//! Machine states, deduced from a scan of the local network: a machine is online when its
//! MAC address answers.

use std::net::Ipv4Addr;

use serde::Serialize;

use crate::config::Machine;
use crate::mac::MacAddress;
use crate::scan::{Device, ScanError};

/// State of a machine, as reported in JSON (see `docs/cli-contract.md`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum State {
    /// The machine answered.
    Online,

    /// The machine did not answer.
    Offline,

    /// The local network could not be scanned.
    Error,
}

impl State {
    /// Stable code of the state, also used in human-readable output.
    pub fn code(self) -> &'static str {
        match self {
            Self::Online => "online",
            Self::Offline => "offline",
            Self::Error => "error",
        }
    }
}

/// Why a machine is in the [`State::Error`] state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Reason {
    /// No network interface is connected to a local network.
    NoLocalNetwork,

    /// The system refused or does not support the scan.
    ScanFailed,
}

impl Reason {
    /// English description of the reason.
    pub fn message(self) -> &'static str {
        match self {
            Self::NoLocalNetwork => "no local network to scan",
            Self::ScanFailed => "the local network cannot be scanned on this system",
        }
    }
}

impl From<ScanError> for Reason {
    fn from(error: ScanError) -> Self {
        match error {
            ScanError::NoLocalNetwork => Self::NoLocalNetwork,
            ScanError::Failed => Self::ScanFailed,
        }
    }
}

/// State of one machine.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Probe {
    /// Resulting state.
    pub state: State,

    /// MAC address that answered, when online.
    pub mac: Option<MacAddress>,

    /// Current IPv4 address of the machine, when online.
    pub address: Option<Ipv4Addr>,

    /// Reason of an error state.
    pub reason: Option<Reason>,
}

/// Deduces the state of each machine from a scan result, in the same order.
///
/// When a MAC address answers on several IP addresses, the lowest one is kept (devices are
/// sorted by address).
pub fn probe_all(machines: &[&Machine], scan: &Result<Vec<Device>, ScanError>) -> Vec<Probe> {
    machines
        .iter()
        .map(|machine| match scan {
            Err(error) => Probe {
                state: State::Error,
                mac: None,
                address: None,
                reason: Some((*error).into()),
            },
            Ok(devices) => match devices.iter().find(|device| device.mac == machine.mac) {
                Some(device) => Probe {
                    state: State::Online,
                    mac: Some(device.mac),
                    address: Some(device.address),
                    reason: None,
                },
                None => Probe {
                    state: State::Offline,
                    mac: None,
                    address: None,
                    reason: None,
                },
            },
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Builds a machine with the given MAC address.
    fn machine(mac: &str) -> Machine {
        Machine {
            name: "Test".to_owned(),
            group: None,
            mac: mac.parse().unwrap(),
            host: "test".to_owned(),
            broadcast: None,
            port: None,
            check_ports: Vec::new(),
        }
    }

    #[test]
    fn states_follow_the_scan() {
        let on = machine("00:11:22:33:44:55");
        let off = machine("00:11:22:33:44:66");
        let devices = vec![Device {
            address: Ipv4Addr::new(192, 168, 1, 20),
            mac: on.mac,
        }];

        let probes = probe_all(&[&on, &off], &Ok(devices));
        assert_eq!(probes[0].state, State::Online);
        assert_eq!(probes[0].address, Some(Ipv4Addr::new(192, 168, 1, 20)));
        assert_eq!(probes[1].state, State::Offline);

        let probes = probe_all(&[&on], &Err(ScanError::NoLocalNetwork));
        assert_eq!(probes[0].state, State::Error);
        assert_eq!(probes[0].reason, Some(Reason::NoLocalNetwork));
    }
}
