//! Machine state checks: ICMP ping first, then TCP connections on the configured ports.

use std::io;
use std::net::{IpAddr, SocketAddr, TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};

use serde::Serialize;

use crate::config::Machine;
use crate::icmp::{self, Ping};

/// State of a machine, as reported in JSON (see `docs/cli-contract.md`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum State {
    /// The machine answered.
    Online,

    /// The machine did not answer.
    Offline,

    /// The state cannot be determined (ICMP unavailable and no TCP port to try).
    Unknown,

    /// The check failed (e.g. the host name cannot be resolved).
    Error,
}

impl State {
    /// Stable code of the state, also used in human-readable output.
    pub fn code(self) -> &'static str {
        match self {
            Self::Online => "online",
            Self::Offline => "offline",
            Self::Unknown => "unknown",
            Self::Error => "error",
        }
    }
}

/// Why a machine is in the [`State::Unknown`] or [`State::Error`] state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Reason {
    /// The host name does not resolve to any address.
    HostNotFound,

    /// ICMP cannot be used here (permissions, IPv6) and no TCP port is configured.
    IcmpUnavailable,
}

impl Reason {
    /// English description of the reason.
    pub fn message(self) -> &'static str {
        match self {
            Self::HostNotFound => "host not found",
            Self::IcmpUnavailable => {
                "ping unavailable on this system and no TCP check port configured"
            }
        }
    }
}

/// How an online machine was detected.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Method {
    /// ICMP echo reply.
    Icmp,

    /// TCP connection accepted or actively refused (both prove the host is up).
    Tcp,
}

/// Result of checking one machine.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Probe {
    /// Resulting state.
    pub state: State,

    /// Address the host name resolved to, when it resolved.
    pub address: Option<IpAddr>,

    /// Detection method, when online.
    pub method: Option<Method>,

    /// TCP port that answered, when detected through TCP.
    pub port: Option<u16>,

    /// Round-trip time (ICMP) or connection time (TCP), when online.
    pub latency: Option<Duration>,

    /// Reason of an unknown or error state.
    pub reason: Option<Reason>,
}

impl Probe {
    /// Builds a probe result without detection details.
    fn without_details(state: State, address: Option<IpAddr>, reason: Option<Reason>) -> Self {
        Self {
            state,
            address,
            method: None,
            port: None,
            latency: None,
            reason,
        }
    }

    /// Builds an online result.
    fn online(address: IpAddr, method: Method, port: Option<u16>, latency: Duration) -> Self {
        Self {
            state: State::Online,
            address: Some(address),
            method: Some(method),
            port,
            latency: Some(latency),
            reason: None,
        }
    }
}

/// Resolves a host name or IP address, preferring IPv4 (ICMP is only implemented for IPv4).
fn resolve(host: &str) -> Option<IpAddr> {
    let addresses: Vec<IpAddr> = (host, 0)
        .to_socket_addrs()
        .ok()?
        .map(|address| address.ip())
        .collect();
    addresses
        .iter()
        .find(|address| address.is_ipv4())
        .or_else(|| addresses.first())
        .copied()
}

/// Checks a machine, waiting up to `timeout` for each attempt (ICMP, then each TCP port).
pub fn probe(machine: &Machine, timeout: Duration) -> Probe {
    let Some(address) = resolve(&machine.host) else {
        return Probe::without_details(State::Error, None, Some(Reason::HostNotFound));
    };

    let ping = match address {
        IpAddr::V4(ipv4) => icmp::ping(ipv4, timeout),
        IpAddr::V6(_) => Ping::Unavailable,
    };
    if let Ping::Reply(latency) = ping {
        return Probe::online(address, Method::Icmp, None, latency);
    }

    for &port in &machine.check_ports {
        let started = Instant::now();
        match TcpStream::connect_timeout(&SocketAddr::new(address, port), timeout) {
            // A refused connection means the host itself answered (TCP reset).
            Ok(_) => return Probe::online(address, Method::Tcp, Some(port), started.elapsed()),
            Err(error) if error.kind() == io::ErrorKind::ConnectionRefused => {
                return Probe::online(address, Method::Tcp, Some(port), started.elapsed());
            }
            Err(_) => {}
        }
    }

    if ping == Ping::Unavailable && machine.check_ports.is_empty() {
        Probe::without_details(State::Unknown, Some(address), Some(Reason::IcmpUnavailable))
    } else {
        Probe::without_details(State::Offline, Some(address), None)
    }
}

/// Checks several machines concurrently (one thread each), returning results in the same order.
pub fn probe_all(machines: &[&Machine], timeout: Duration) -> Vec<Probe> {
    std::thread::scope(|scope| {
        let handles: Vec<_> = machines
            .iter()
            .map(|machine| scope.spawn(move || probe(machine, timeout)))
            .collect();
        handles
            .into_iter()
            .map(|handle| handle.join().expect("probe threads do not panic"))
            .collect()
    })
}

#[cfg(test)]
mod tests {
    use std::net::{Ipv4Addr, TcpListener};

    use super::*;

    /// Builds a machine checked on `host` and `check_ports`.
    fn machine(host: &str, check_ports: Vec<u16>) -> Machine {
        Machine {
            name: "Test".to_owned(),
            group: None,
            mac: "00:11:22:33:44:55".parse().unwrap(),
            host: host.to_owned(),
            broadcast: None,
            port: None,
            check_ports,
        }
    }

    #[test]
    fn unresolvable_host_is_an_error() {
        let result = probe(
            &machine("does-not-exist.invalid", Vec::new()),
            Duration::from_millis(500),
        );
        assert_eq!(result.state, State::Error);
        assert_eq!(result.reason, Some(Reason::HostNotFound));
    }

    #[test]
    fn listening_port_proves_host_is_online() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();

        let result = probe(&machine("127.0.0.1", vec![port]), Duration::from_secs(2));
        assert_eq!(result.state, State::Online);
        assert!(result.latency.is_some());
    }

    #[test]
    fn unreachable_host_is_offline_or_unknown() {
        // TEST-NET-1 (RFC 5737): reserved, never answers.
        let result = probe(
            &machine("192.0.2.1", Vec::new()),
            Duration::from_millis(300),
        );
        assert!(
            matches!(result.state, State::Offline | State::Unknown),
            "{result:?}"
        );
    }
}
