//! Machine states, deduced from a scan of the local network: a machine is online when any of
//! its network interfaces answers.

use std::net::Ipv4Addr;

use serde::Serialize;

use crate::config::{Interface, Machine};
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

    /// Interface that answered (the first one in configuration order), when online.
    pub interface: Option<Interface>,

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
                interface: None,
                address: None,
                reason: Some((*error).into()),
            },
            Ok(devices) => match machine.interfaces.iter().find_map(|interface| {
                devices
                    .iter()
                    .find(|device| device.mac == interface.mac)
                    .map(|device| (interface, device))
            }) {
                Some((interface, device)) => Probe {
                    state: State::Online,
                    interface: Some(interface.clone()),
                    address: Some(device.address),
                    reason: None,
                },
                None => Probe {
                    state: State::Offline,
                    interface: None,
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

    /// Builds a machine with the given MAC addresses (labels "1", "2"...).
    fn machine(macs: &[&str]) -> Machine {
        Machine {
            name: "Test".to_owned(),
            group: None,
            interfaces: macs
                .iter()
                .enumerate()
                .map(|(index, mac)| Interface {
                    mac: mac.parse().unwrap(),
                    label: Some((index + 1).to_string()),
                })
                .collect(),
            broadcast: None,
            port: None,
        }
    }

    #[test]
    fn states_follow_the_scan() {
        let on = machine(&["00:11:22:33:44:55", "00:11:22:33:44:56"]);
        let off = machine(&["00:11:22:33:44:66"]);
        let devices = vec![Device {
            address: Ipv4Addr::new(192, 168, 1, 20),
            mac: on.interfaces[1].mac,
        }];

        let probes = probe_all(&[&on, &off], &Ok(devices));
        assert_eq!(probes[0].state, State::Online);
        assert_eq!(probes[0].address, Some(Ipv4Addr::new(192, 168, 1, 20)));
        assert_eq!(probes[0].interface.as_ref(), Some(&on.interfaces[1]));
        assert_eq!(probes[1].state, State::Offline);

        let probes = probe_all(&[&on], &Err(ScanError::NoLocalNetwork));
        assert_eq!(probes[0].state, State::Error);
        assert_eq!(probes[0].reason, Some(Reason::NoLocalNetwork));
    }
}
