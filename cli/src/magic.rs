//! Wake-on-LAN magic packets.

use std::io;
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4, UdpSocket};

use crate::mac::MacAddress;

/// Size of a magic packet: 6 synchronization bytes followed by 16 copies of the MAC address.
pub const PACKET_LEN: usize = 102;

/// Default UDP port for magic packets ("discard" port, the most widely accepted).
pub const DEFAULT_PORT: u16 = 9;

/// Default destination when no broadcast address is configured (limited broadcast).
pub const DEFAULT_BROADCAST: Ipv4Addr = Ipv4Addr::BROADCAST;

/// Number of copies sent per wake-up, since UDP gives no delivery guarantee.
const REPEAT: usize = 3;

/// Builds the magic packet that wakes the machine with the given MAC address.
pub fn packet(mac: MacAddress) -> [u8; PACKET_LEN] {
    let mut packet = [0xFF; PACKET_LEN];
    for chunk in packet[6..].chunks_exact_mut(6) {
        chunk.copy_from_slice(&mac.octets());
    }
    packet
}

/// Sends the magic packet for `mac` to `target`, repeated a few times.
///
/// # Errors
///
/// Returns the underlying I/O error when the socket cannot be opened or the packet cannot be sent.
pub fn send(mac: MacAddress, target: SocketAddrV4) -> io::Result<()> {
    let socket = UdpSocket::bind(SocketAddr::from((Ipv4Addr::UNSPECIFIED, 0)))?;
    socket.set_broadcast(true)?;

    let packet = packet(mac);
    for _ in 0..REPEAT {
        socket.send_to(&packet, target)?;
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn packet_starts_with_sync_bytes_then_repeats_mac() {
        let mac = MacAddress([0xA4, 0xBB, 0x6D, 0x12, 0x34, 0x56]);
        let packet = packet(mac);

        assert_eq!(&packet[..6], &[0xFF; 6]);
        for chunk in packet[6..].chunks_exact(6) {
            assert_eq!(chunk, mac.octets());
        }
    }

    #[test]
    fn send_delivers_packet() {
        let receiver = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let SocketAddr::V4(target) = receiver.local_addr().unwrap() else {
            unreachable!("bound to an IPv4 address");
        };
        let mac = MacAddress([1, 2, 3, 4, 5, 6]);

        send(mac, target).unwrap();

        let mut buffer = [0u8; 256];
        let (len, _) = receiver.recv_from(&mut buffer).unwrap();
        assert_eq!(&buffer[..len], &packet(mac));
    }
}
