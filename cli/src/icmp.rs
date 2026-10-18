//! ICMP echo (ping) without administrator rights.
//!
//! - Windows: the `IcmpSendEcho` system API, available to every user.
//! - Linux / macOS: unprivileged ICMP datagram sockets. On Linux they require the user's group
//!   to be allowed by `net.ipv4.ping_group_range`; otherwise [`Ping::Unavailable`] is returned
//!   and callers fall back to TCP checks.

use std::net::Ipv4Addr;
use std::time::Duration;

/// Outcome of a ping.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Ping {
    /// The host answered, after this round-trip time.
    Reply(Duration),

    /// No answer before the timeout (host down, or ICMP filtered).
    NoReply,

    /// ICMP cannot be used on this system (permissions, unsupported platform).
    Unavailable,
}

/// Sends one ICMP echo request to `address` and waits up to `timeout` for the reply.
pub fn ping(address: Ipv4Addr, timeout: Duration) -> Ping {
    platform::ping(address, timeout)
}

/// Payload carried by echo requests, used to recognize our own replies.
const PAYLOAD: &[u8; 16] = b"wol status probe";

#[cfg(windows)]
mod platform {
    use std::ffi::c_void;
    use std::net::Ipv4Addr;
    use std::time::Duration;

    use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        ICMP_ECHO_REPLY, IP_SUCCESS, IcmpCloseHandle, IcmpCreateFile, IcmpSendEcho,
    };

    use super::{PAYLOAD, Ping};

    /// Pings through `IcmpSendEcho`.
    pub fn ping(address: Ipv4Addr, timeout: Duration) -> Ping {
        // SAFETY: IcmpCreateFile has no preconditions; the handle is checked before use.
        let handle = unsafe { IcmpCreateFile() };
        if handle.is_null() || handle == INVALID_HANDLE_VALUE {
            return Ping::Unavailable;
        }

        // The reply buffer must hold one ICMP_ECHO_REPLY, the echoed payload and 8 bytes
        // for a possible ICMP error message (documented minimum).
        let mut buffer = vec![0u8; size_of::<ICMP_ECHO_REPLY>() + PAYLOAD.len() + 8];
        let timeout_ms = u32::try_from(timeout.as_millis())
            .unwrap_or(u32::MAX)
            .max(1);

        // SAFETY: every pointer refers to a live buffer of the advertised size; the
        // destination address is in network byte order as the API requires.
        let replies = unsafe {
            IcmpSendEcho(
                handle,
                u32::from_ne_bytes(address.octets()),
                PAYLOAD.as_ptr().cast::<c_void>(),
                PAYLOAD.len() as u16,
                std::ptr::null(),
                buffer.as_mut_ptr().cast::<c_void>(),
                buffer.len() as u32,
                timeout_ms,
            )
        };

        let result = if replies == 0 {
            Ping::NoReply
        } else {
            // SAFETY: on success the buffer starts with an ICMP_ECHO_REPLY; read_unaligned
            // because a Vec<u8> gives no alignment guarantee.
            let reply = unsafe { buffer.as_ptr().cast::<ICMP_ECHO_REPLY>().read_unaligned() };
            if reply.Status == IP_SUCCESS {
                Ping::Reply(Duration::from_millis(u64::from(reply.RoundTripTime)))
            } else {
                Ping::NoReply
            }
        };

        // SAFETY: the handle was opened above and is closed once.
        unsafe { IcmpCloseHandle(handle) };
        result
    }
}

#[cfg(unix)]
mod platform {
    use std::io;
    use std::net::{Ipv4Addr, SocketAddr, UdpSocket};
    use std::sync::atomic::{AtomicU16, Ordering};
    use std::time::{Duration, Instant};

    use socket2::{Domain, Protocol, Socket, Type};

    use super::{PAYLOAD, Ping};

    /// Counter distinguishing the echo requests sent by this process.
    static SEQUENCE: AtomicU16 = AtomicU16::new(0);

    /// ICMP message type of an echo request.
    const ECHO_REQUEST: u8 = 8;

    /// ICMP message type of an echo reply.
    const ECHO_REPLY: u8 = 0;

    /// Pings through an unprivileged ICMP datagram socket.
    pub fn ping(address: Ipv4Addr, timeout: Duration) -> Ping {
        let Ok(socket) = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::ICMPV4)) else {
            return Ping::Unavailable;
        };
        // A datagram socket of any protocol can be driven through UdpSocket's send / recv.
        let socket: UdpSocket = socket.into();

        // Concurrent pings from this process share the socket type: give each its own sequence.
        let sequence =
            (std::process::id() as u16).wrapping_add(SEQUENCE.fetch_add(1, Ordering::Relaxed));
        let request = echo_request(sequence);
        let started = Instant::now();
        if socket
            .send_to(&request, SocketAddr::from((address, 0)))
            .is_err()
        {
            return Ping::NoReply;
        }

        let mut buffer = [0u8; 1500];
        loop {
            let Some(remaining) = timeout
                .checked_sub(started.elapsed())
                .filter(|left| !left.is_zero())
            else {
                return Ping::NoReply;
            };
            if socket.set_read_timeout(Some(remaining)).is_err() {
                return Ping::NoReply;
            }
            match socket.recv(&mut buffer) {
                Ok(len) if is_our_reply(&buffer[..len], sequence) => {
                    return Ping::Reply(started.elapsed());
                }
                Ok(_) => continue,
                Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                Err(_) => return Ping::NoReply,
            }
        }
    }

    /// Builds an echo request carrying [`PAYLOAD`].
    fn echo_request(sequence: u16) -> Vec<u8> {
        let mut packet = vec![ECHO_REQUEST, 0, 0, 0, 0, 0];
        packet.extend_from_slice(&sequence.to_be_bytes());
        packet.extend_from_slice(PAYLOAD);
        let checksum = checksum(&packet);
        packet[2..4].copy_from_slice(&checksum.to_be_bytes());
        packet
    }

    /// Tells whether `packet` is the reply to our request.
    ///
    /// Linux delivers the bare ICMP message; macOS prepends the IPv4 header, skipped here.
    /// The identifier is not compared: Linux rewrites it with the socket's own.
    fn is_our_reply(packet: &[u8], sequence: u16) -> bool {
        let icmp = match packet.first() {
            Some(byte) if byte >> 4 == 4 => packet
                .get(usize::from(byte & 0x0F) * 4..)
                .unwrap_or_default(),
            _ => packet,
        };
        icmp.len() >= 8 + PAYLOAD.len()
            && icmp[0] == ECHO_REPLY
            && icmp[6..8] == sequence.to_be_bytes()
            && &icmp[8..8 + PAYLOAD.len()] == PAYLOAD
    }

    /// Computes the Internet checksum (RFC 1071).
    fn checksum(data: &[u8]) -> u16 {
        let mut sum: u32 = data
            .chunks(2)
            .map(|pair| u32::from(u16::from_be_bytes([pair[0], *pair.get(1).unwrap_or(&0)])))
            .sum();
        while sum > 0xFFFF {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        !(sum as u16)
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn request_checksum_verifies() {
            assert_eq!(checksum(&echo_request(42)), 0);
        }

        #[test]
        fn recognizes_reply_with_or_without_ip_header() {
            let mut reply = echo_request(7);
            reply[0] = ECHO_REPLY;
            assert!(is_our_reply(&reply, 7));
            assert!(!is_our_reply(&reply, 8));

            let mut with_header = vec![0x45; 1];
            with_header.extend_from_slice(&[0; 19]);
            with_header.extend_from_slice(&reply);
            assert!(is_our_reply(&with_header, 7));
        }
    }
}

#[cfg(not(any(windows, unix)))]
mod platform {
    use std::net::Ipv4Addr;
    use std::time::Duration;

    use super::Ping;

    /// ICMP is not supported on this platform.
    pub fn ping(_address: Ipv4Addr, _timeout: Duration) -> Ping {
        Ping::Unavailable
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loopback_answers_or_icmp_is_unavailable() {
        let result = ping(Ipv4Addr::LOCALHOST, Duration::from_secs(2));
        assert!(
            matches!(result, Ping::Reply(_) | Ping::Unavailable),
            "{result:?}"
        );
    }
}
