//! Local network scan: which MAC addresses are present on the local network, and at which
//! IPv4 address, without administrator rights.
//!
//! - Windows: `SendARP` for every address of each local subnet (one short-lived thread each).
//! - Linux: a UDP datagram to every address makes the kernel resolve it (ARP); the neighbor
//!   table is then read through netlink, keeping only entries the kernel just confirmed.
//! - macOS: the same UDP trigger, then the ARP table is read through `sysctl`. macOS does not
//!   tell whether an entry is fresh: a machine that just went off may still appear present
//!   until its entry expires (about 20 minutes).

use std::net::Ipv4Addr;

use crate::mac::MacAddress;

/// A device found on the local network.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct Device {
    /// Current IPv4 address of the device.
    pub address: Ipv4Addr,

    /// MAC address that answered.
    pub mac: MacAddress,
}

/// Why the local network could not be scanned.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScanError {
    /// No IPv4 network interface is connected.
    NoLocalNetwork,

    /// The system refused or does not support the scan.
    Failed,
}

/// Smallest prefix scanned in full (/22, 1022 addresses); larger subnets are reduced to the
/// /24 around the interface's own address, to keep a scan within a few seconds.
const MIN_PREFIX: u32 = 22;

/// Port the Linux / macOS trigger datagrams are sent to ("discard": nothing answers there).
#[cfg(any(target_os = "linux", target_os = "macos"))]
const DISCARD_PORT: u16 = 9;

/// Test hook: when this variable is set, the network is not scanned and its value describes
/// the devices found, as `IP=MAC` pairs separated by commas (`none`: no local network).
pub const FAKE_SCAN_ENV: &str = "WOL_FAKE_SCAN";

/// A local IPv4 subnet, as configured on a network interface.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Subnet {
    /// Address of the interface.
    address: Ipv4Addr,

    /// Subnet mask of the interface.
    mask: Ipv4Addr,
}

impl Subnet {
    /// Addresses to probe: every host address of the subnet except the interface's own.
    fn hosts(self) -> Vec<Ipv4Addr> {
        let address = u32::from(self.address);
        let mut mask = u32::from(self.mask);
        if mask.leading_ones() != mask.count_ones() || mask.count_ones() < MIN_PREFIX {
            mask = 0xFFFF_FF00;
        }
        if mask.count_ones() > 30 {
            return Vec::new();
        }

        let network = address & mask;
        let broadcast = network | !mask;
        (network + 1..broadcast)
            .filter(|&host| host != address)
            .map(Ipv4Addr::from)
            .collect()
    }

    /// Tells whether the subnet belongs to a real local network (not loopback nor link-local).
    fn is_usable(self) -> bool {
        !self.address.is_loopback()
            && !self.address.is_link_local()
            && !self.address.is_unspecified()
    }
}

/// Scans the local network and returns the devices found, sorted by address.
///
/// # Errors
///
/// Returns [`ScanError::NoLocalNetwork`] when no network interface is connected, and
/// [`ScanError::Failed`] when the system refuses the scan.
pub fn scan() -> Result<Vec<Device>, ScanError> {
    if let Some(fake) = std::env::var_os(FAKE_SCAN_ENV) {
        return fake_scan(&fake.to_string_lossy());
    }

    let mut subnets: Vec<Subnet> = platform::subnets()?
        .into_iter()
        .filter(|subnet| subnet.is_usable())
        .collect();
    subnets.dedup();
    if subnets.is_empty() {
        return Err(ScanError::NoLocalNetwork);
    }

    let mut hosts: Vec<Ipv4Addr> = subnets.iter().flat_map(|subnet| subnet.hosts()).collect();
    hosts.sort_unstable();
    hosts.dedup();

    let mut devices: Vec<Device> = platform::resolve(&hosts)?
        .into_iter()
        .filter(|device| device.mac.octets() != [0; 6] && device.mac.octets() != [0xFF; 6])
        .collect();
    devices.sort_unstable();
    devices.dedup();
    Ok(devices)
}

/// Builds a scan result from the value of [`FAKE_SCAN_ENV`]; invalid pairs are skipped.
fn fake_scan(value: &str) -> Result<Vec<Device>, ScanError> {
    if value == "none" {
        return Err(ScanError::NoLocalNetwork);
    }
    let mut devices: Vec<Device> = value
        .split(',')
        .filter_map(|pair| {
            let (address, mac) = pair.trim().split_once('=')?;
            Some(Device {
                address: address.parse().ok()?,
                mac: mac.parse().ok()?,
            })
        })
        .collect();
    devices.sort_unstable();
    Ok(devices)
}

#[cfg(windows)]
mod platform {
    use std::net::Ipv4Addr;
    use std::sync::mpsc;
    use std::time::{Duration, Instant};

    use windows_sys::Win32::Foundation::{ERROR_BUFFER_OVERFLOW, NO_ERROR};
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        GAA_FLAG_INCLUDE_GATEWAYS, GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER,
        GAA_FLAG_SKIP_MULTICAST, GetAdaptersAddresses, IP_ADAPTER_ADDRESSES_LH, SendARP,
    };
    use windows_sys::Win32::NetworkManagement::Ndis::IfOperStatusUp;
    use windows_sys::Win32::Networking::WinSock::{AF_INET, SOCKADDR};

    use super::{Device, ScanError, Subnet};
    use crate::mac::MacAddress;

    /// Stack size of the resolver threads: they only wait on one system call.
    const STACK_SIZE: usize = 64 * 1024;

    /// How long answers are collected. Present devices answer within milliseconds, whereas
    /// `SendARP` takes seconds to give up on an empty address: those are not waited for.
    const DEADLINE: Duration = Duration::from_millis(1_500);

    /// Lists the IPv4 subnets of connected adapters through `GetAdaptersAddresses`.
    ///
    /// Adapters with a default gateway are preferred: the others are usually virtual switches
    /// (Hyper-V, WSL, VPN) whose scan would only slow things down. When no adapter has a
    /// gateway (isolated network), every connected adapter is kept.
    pub fn subnets() -> Result<Vec<Subnet>, ScanError> {
        let flags = GAA_FLAG_SKIP_ANYCAST
            | GAA_FLAG_SKIP_MULTICAST
            | GAA_FLAG_SKIP_DNS_SERVER
            | GAA_FLAG_INCLUDE_GATEWAYS;
        let mut size = 16 * 1024u32;
        let mut buffer: Vec<u64>;
        loop {
            // u64 elements give the structures the alignment their fields need.
            buffer = vec![0u64; (size as usize).div_ceil(8)];
            // SAFETY: the buffer holds `size` bytes.
            let result = unsafe {
                GetAdaptersAddresses(
                    u32::from(AF_INET),
                    flags,
                    std::ptr::null(),
                    buffer.as_mut_ptr().cast(),
                    &mut size,
                )
            };
            match result {
                NO_ERROR => break,
                ERROR_BUFFER_OVERFLOW => continue,
                _ => return Err(ScanError::Failed),
            }
        }

        let mut routed = Vec::new();
        let mut others = Vec::new();
        let mut adapter = buffer.as_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
        while !adapter.is_null() {
            // SAFETY: `adapter` is a node of the list GetAdaptersAddresses wrote in the buffer,
            // which outlives this loop; so are the address nodes it points to.
            let current = unsafe { &*adapter };
            if current.OperStatus == IfOperStatusUp {
                let target = if current.FirstGatewayAddress.is_null() {
                    &mut others
                } else {
                    &mut routed
                };
                let mut unicast = current.FirstUnicastAddress;
                while !unicast.is_null() {
                    // SAFETY: see above.
                    let address = unsafe { &*unicast };
                    // SAFETY: the socket address comes from the same list.
                    if let Some(ip) = unsafe { ipv4(address.Address.lpSockaddr) } {
                        let prefix = u32::from(address.OnLinkPrefixLength.min(32));
                        let mask = u32::MAX.checked_shl(32 - prefix).unwrap_or(0);
                        target.push(Subnet {
                            address: ip,
                            mask: Ipv4Addr::from(mask),
                        });
                    }
                    unicast = address.Next;
                }
            }
            adapter = current.Next;
        }

        Ok(if routed.is_empty() { others } else { routed })
    }

    /// Reads an IPv4 address from a socket address.
    ///
    /// # Safety
    ///
    /// `address` must be null or point to a valid socket address.
    unsafe fn ipv4(address: *const SOCKADDR) -> Option<Ipv4Addr> {
        if address.is_null() {
            return None;
        }
        // SAFETY: guaranteed by the caller; an AF_INET socket address is a SOCKADDR_IN, whose
        // bytes 4 to 7 hold the address in network byte order.
        unsafe {
            if (*address).sa_family != AF_INET {
                return None;
            }
            let bytes = address.cast::<[u8; 8]>().read_unaligned();
            Some(Ipv4Addr::new(bytes[4], bytes[5], bytes[6], bytes[7]))
        }
    }

    /// Resolves every address with `SendARP`, one thread each, collecting answers until
    /// [`DEADLINE`]; threads still waiting on empty addresses end on their own afterwards.
    pub fn resolve(hosts: &[Ipv4Addr]) -> Result<Vec<Device>, ScanError> {
        let (sender, receiver) = mpsc::channel();
        let mut spawned = 0usize;
        for &host in hosts {
            let sender = sender.clone();
            let thread = std::thread::Builder::new()
                .stack_size(STACK_SIZE)
                .spawn(move || {
                    let _ = sender.send(arp(host));
                });
            spawned += usize::from(thread.is_ok());
        }
        drop(sender);

        let deadline = Instant::now() + DEADLINE;
        let mut devices = Vec::new();
        for _ in 0..spawned {
            let Some(remaining) = deadline.checked_duration_since(Instant::now()) else {
                break;
            };
            match receiver.recv_timeout(remaining) {
                Ok(device) => devices.extend(device),
                Err(_) => break,
            }
        }
        Ok(devices)
    }

    /// Sends one ARP request and returns the device when it answers.
    fn arp(host: Ipv4Addr) -> Option<Device> {
        let mut buffer = [0u8; 8];
        let mut len = buffer.len() as u32;
        // SAFETY: the buffer holds `len` bytes, as SendARP requires; the destination is in
        // network byte order.
        let result = unsafe {
            SendARP(
                u32::from_ne_bytes(host.octets()),
                0,
                buffer.as_mut_ptr().cast(),
                &mut len,
            )
        };
        if result != NO_ERROR || len != 6 {
            return None;
        }

        let mut mac = [0u8; 6];
        mac.copy_from_slice(&buffer[..6]);
        Some(Device {
            address: host,
            mac: MacAddress(mac),
        })
    }
}

/// Code shared by Linux and macOS: interface listing and resolution trigger.
#[cfg(any(target_os = "linux", target_os = "macos"))]
mod unix {
    use std::net::{Ipv4Addr, UdpSocket};

    use super::{DISCARD_PORT, ScanError, Subnet};

    /// Lists the IPv4 addresses of interfaces that are up, through `getifaddrs`.
    pub fn subnets() -> Result<Vec<Subnet>, ScanError> {
        let mut addresses: *mut libc::ifaddrs = std::ptr::null_mut();
        // SAFETY: getifaddrs fills the pointer with a list freed below with freeifaddrs.
        if unsafe { libc::getifaddrs(&mut addresses) } != 0 {
            return Err(ScanError::Failed);
        }

        let mut subnets = Vec::new();
        let mut current = addresses;
        while !current.is_null() {
            // SAFETY: `current` is a node of the list returned by getifaddrs.
            let entry = unsafe { &*current };
            let flags = entry.ifa_flags;
            let up = flags & libc::IFF_UP as libc::c_uint != 0
                && flags & libc::IFF_RUNNING as libc::c_uint != 0
                && flags & libc::IFF_LOOPBACK as libc::c_uint == 0;
            // SAFETY: both pointers come from getifaddrs and are checked by ipv4().
            let address = unsafe { ipv4(entry.ifa_addr, true) };
            let mask = unsafe { ipv4(entry.ifa_netmask, false) };
            if let (true, Some(address), Some(mask)) = (up, address, mask) {
                subnets.push(Subnet { address, mask });
            }
            current = entry.ifa_next;
        }

        // SAFETY: the list was returned by getifaddrs and is no longer used.
        unsafe { libc::freeifaddrs(addresses) };
        Ok(subnets)
    }

    /// Reads an IPv4 address from a socket address.
    ///
    /// Netmasks may carry no address family on macOS: `check_family` is false for them.
    ///
    /// # Safety
    ///
    /// `address` must be null or point to a socket address of at least `sockaddr_in` size
    /// when its family is `AF_INET` (or when `check_family` is false).
    unsafe fn ipv4(address: *const libc::sockaddr, check_family: bool) -> Option<Ipv4Addr> {
        if address.is_null() {
            return None;
        }
        // SAFETY: guaranteed by the caller.
        unsafe {
            if check_family && i32::from((*address).sa_family) != libc::AF_INET {
                return None;
            }
            let address = address.cast::<libc::sockaddr_in>().read_unaligned();
            Some(Ipv4Addr::from(u32::from_be(address.sin_addr.s_addr)))
        }
    }

    /// Sends one empty-ish UDP datagram to every address, so that the kernel resolves their
    /// MAC addresses (ARP). Send errors are irrelevant: only the resolution matters.
    pub fn trigger(hosts: &[Ipv4Addr]) {
        let Ok(socket) = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0)) else {
            return;
        };
        for &host in hosts {
            let _ = socket.send_to(&[0], (host, DISCARD_PORT));
        }
    }
}

#[cfg(target_os = "linux")]
mod platform {
    use std::collections::HashSet;
    use std::io;
    use std::net::Ipv4Addr;
    use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
    use std::time::{Duration, Instant};

    use super::{Device, ScanError, Subnet, unix};
    use crate::mac::MacAddress;

    /// Size of `struct ndmsg`, which follows the netlink header of neighbor messages.
    const NDMSG_LEN: usize = 12;

    /// Neighbor states that are still being resolved or confirmed.
    const PENDING: u16 = libc::NUD_INCOMPLETE | libc::NUD_DELAY | libc::NUD_PROBE;

    /// Neighbor states proving the device answered recently.
    const CONFIRMED: u16 = libc::NUD_REACHABLE | libc::NUD_PERMANENT;

    /// Longest wait for pending entries: a stale entry waits 5 s (`delay_first_probe_time`)
    /// before the kernel probes it.
    const DEADLINE: Duration = Duration::from_secs(8);

    /// A neighbor table entry.
    struct Neighbor {
        /// IPv4 address.
        address: Ipv4Addr,

        /// MAC address, when known.
        mac: Option<MacAddress>,

        /// `NUD_*` state.
        state: u16,
    }

    /// Lists local subnets.
    pub fn subnets() -> Result<Vec<Subnet>, ScanError> {
        unix::subnets()
    }

    /// Triggers resolution of every address, then waits until the kernel settled them.
    pub fn resolve(hosts: &[Ipv4Addr]) -> Result<Vec<Device>, ScanError> {
        let wanted: HashSet<Ipv4Addr> = hosts.iter().copied().collect();
        let started = Instant::now();
        unix::trigger(hosts);

        loop {
            std::thread::sleep(Duration::from_millis(250));
            let entries = neighbors().map_err(|_| ScanError::Failed)?;
            let pending = entries
                .iter()
                .any(|entry| wanted.contains(&entry.address) && entry.state & PENDING != 0);

            if !pending || started.elapsed() >= DEADLINE {
                return Ok(entries
                    .into_iter()
                    .filter(|entry| wanted.contains(&entry.address) && entry.state & CONFIRMED != 0)
                    .filter_map(|entry| {
                        Some(Device {
                            address: entry.address,
                            mac: entry.mac?,
                        })
                    })
                    .collect());
            }
        }
    }

    /// Dumps the IPv4 neighbor table through a netlink route socket (no privileges needed).
    fn neighbors() -> io::Result<Vec<Neighbor>> {
        // SAFETY: plain socket creation; the descriptor is owned right away.
        let fd = unsafe {
            libc::socket(
                libc::AF_NETLINK,
                libc::SOCK_RAW | libc::SOCK_CLOEXEC,
                libc::NETLINK_ROUTE,
            )
        };
        if fd < 0 {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: `fd` is a fresh, valid descriptor nobody else owns.
        let socket = unsafe { OwnedFd::from_raw_fd(fd) };

        let header_len = size_of::<libc::nlmsghdr>();
        let mut request = vec![0u8; header_len + NDMSG_LEN];
        let header = libc::nlmsghdr {
            nlmsg_len: request.len() as u32,
            nlmsg_type: libc::RTM_GETNEIGH,
            nlmsg_flags: (libc::NLM_F_REQUEST | libc::NLM_F_DUMP) as u16,
            nlmsg_seq: 1,
            nlmsg_pid: 0,
        };
        // SAFETY: the request buffer is large enough for the header.
        unsafe {
            request
                .as_mut_ptr()
                .cast::<libc::nlmsghdr>()
                .write_unaligned(header)
        };
        request[header_len] = libc::AF_INET as u8;

        // SAFETY: the pointer and length describe the request buffer.
        let sent = unsafe {
            libc::send(
                socket.as_raw_fd(),
                request.as_ptr().cast(),
                request.len(),
                0,
            )
        };
        if sent < 0 {
            return Err(io::Error::last_os_error());
        }

        let mut neighbors = Vec::new();
        let mut buffer = vec![0u8; 64 * 1024];
        loop {
            // SAFETY: the pointer and length describe the receive buffer.
            let received = unsafe {
                libc::recv(
                    socket.as_raw_fd(),
                    buffer.as_mut_ptr().cast(),
                    buffer.len(),
                    0,
                )
            };
            if received < 0 {
                return Err(io::Error::last_os_error());
            }
            let data = &buffer[..received as usize];
            if data.is_empty() {
                return Ok(neighbors);
            }

            let mut offset = 0;
            while offset + header_len <= data.len() {
                // SAFETY: at least `header_len` bytes remain at `offset`.
                let header = unsafe {
                    data.as_ptr()
                        .add(offset)
                        .cast::<libc::nlmsghdr>()
                        .read_unaligned()
                };
                let length = header.nlmsg_len as usize;
                if length < header_len || offset + length > data.len() {
                    return Ok(neighbors);
                }
                match i32::from(header.nlmsg_type) {
                    libc::NLMSG_DONE => return Ok(neighbors),
                    libc::NLMSG_ERROR => return Err(io::Error::other("netlink error")),
                    _ if header.nlmsg_type == libc::RTM_NEWNEIGH => {
                        neighbors
                            .extend(parse_neighbor(&data[offset + header_len..offset + length]));
                    }
                    _ => {}
                }
                offset += align(length);
            }
        }
    }

    /// Rounds a netlink length up to the 4-byte alignment of messages and attributes.
    fn align(length: usize) -> usize {
        (length + 3) & !3
    }

    /// Parses the payload of an `RTM_NEWNEIGH` message: `struct ndmsg`, then attributes.
    fn parse_neighbor(payload: &[u8]) -> Option<Neighbor> {
        if payload.len() < NDMSG_LEN || i32::from(payload[0]) != libc::AF_INET {
            return None;
        }
        let state = u16::from_ne_bytes([payload[8], payload[9]]);

        let mut address = None;
        let mut mac = None;
        let mut offset = NDMSG_LEN;
        while offset + 4 <= payload.len() {
            let length = usize::from(u16::from_ne_bytes([payload[offset], payload[offset + 1]]));
            let kind = u16::from_ne_bytes([payload[offset + 2], payload[offset + 3]]);
            if length < 4 || offset + length > payload.len() {
                break;
            }
            let value = &payload[offset + 4..offset + length];
            match (kind, value.len()) {
                (libc::NDA_DST, 4) => {
                    address = Some(Ipv4Addr::new(value[0], value[1], value[2], value[3]))
                }
                (libc::NDA_LLADDR, 6) => {
                    let mut octets = [0u8; 6];
                    octets.copy_from_slice(value);
                    mac = Some(MacAddress(octets));
                }
                _ => {}
            }
            offset += align(length);
        }

        Some(Neighbor {
            address: address?,
            mac,
            state,
        })
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn parses_neighbor_message() {
            let mut payload = vec![libc::AF_INET as u8, 0, 0, 0, 2, 0, 0, 0];
            payload.extend_from_slice(&libc::NUD_REACHABLE.to_ne_bytes());
            payload.extend_from_slice(&[0, 0]);
            payload.extend_from_slice(&8u16.to_ne_bytes());
            payload.extend_from_slice(&libc::NDA_DST.to_ne_bytes());
            payload.extend_from_slice(&[192, 168, 1, 35]);
            payload.extend_from_slice(&10u16.to_ne_bytes());
            payload.extend_from_slice(&libc::NDA_LLADDR.to_ne_bytes());
            payload.extend_from_slice(&[1, 2, 3, 4, 5, 6, 0, 0]);

            let neighbor = parse_neighbor(&payload).unwrap();
            assert_eq!(neighbor.address, Ipv4Addr::new(192, 168, 1, 35));
            assert_eq!(neighbor.mac, Some(MacAddress([1, 2, 3, 4, 5, 6])));
            assert_eq!(neighbor.state, libc::NUD_REACHABLE);
        }
    }
}

#[cfg(target_os = "macos")]
mod platform {
    use std::collections::HashSet;
    use std::io;
    use std::net::Ipv4Addr;
    use std::time::Duration;

    use super::{Device, ScanError, Subnet, unix};
    use crate::mac::MacAddress;

    /// Time given to the kernel to resolve the triggered addresses.
    const SETTLE: Duration = Duration::from_millis(1_500);

    /// Lists local subnets.
    pub fn subnets() -> Result<Vec<Subnet>, ScanError> {
        unix::subnets()
    }

    /// Triggers resolution of every address, waits, then reads the ARP table.
    pub fn resolve(hosts: &[Ipv4Addr]) -> Result<Vec<Device>, ScanError> {
        let wanted: HashSet<Ipv4Addr> = hosts.iter().copied().collect();
        unix::trigger(hosts);
        std::thread::sleep(SETTLE);

        Ok(arp_table()
            .map_err(|_| ScanError::Failed)?
            .into_iter()
            .filter(|device| wanted.contains(&device.address))
            .collect())
    }

    /// Reads the ARP table through `sysctl` (`NET_RT_FLAGS` / `RTF_LLINFO`).
    fn arp_table() -> io::Result<Vec<Device>> {
        let mut mib = [
            libc::CTL_NET,
            libc::PF_ROUTE,
            0,
            libc::AF_INET,
            libc::NET_RT_FLAGS,
            libc::RTF_LLINFO,
        ];
        let mut size = 0usize;
        // SAFETY: a null buffer only asks for the required size.
        let result = unsafe {
            libc::sysctl(
                mib.as_mut_ptr(),
                mib.len() as u32,
                std::ptr::null_mut(),
                &mut size,
                std::ptr::null_mut(),
                0,
            )
        };
        if result != 0 {
            return Err(io::Error::last_os_error());
        }

        // The table may grow between both calls: leave some room.
        let mut buffer = vec![0u8; size + size / 4 + 1];
        size = buffer.len();
        // SAFETY: the buffer holds `size` bytes.
        let result = unsafe {
            libc::sysctl(
                mib.as_mut_ptr(),
                mib.len() as u32,
                buffer.as_mut_ptr().cast(),
                &mut size,
                std::ptr::null_mut(),
                0,
            )
        };
        if result != 0 {
            return Err(io::Error::last_os_error());
        }

        let data = &buffer[..size];
        let header_len = size_of::<libc::rt_msghdr>();
        let mut devices = Vec::new();
        let mut offset = 0;
        while offset + header_len <= data.len() {
            // SAFETY: at least `header_len` bytes remain at `offset`.
            let header = unsafe {
                data.as_ptr()
                    .add(offset)
                    .cast::<libc::rt_msghdr>()
                    .read_unaligned()
            };
            let length = usize::from(header.rtm_msglen);
            if length < header_len || offset + length > data.len() {
                break;
            }
            devices.extend(parse_entry(&data[offset + header_len..offset + length]));
            offset += length;
        }
        Ok(devices)
    }

    /// Rounds a socket address length up like the kernel's `ROUNDUP` (4-byte alignment).
    fn round_up(length: usize) -> usize {
        if length == 0 { 4 } else { (length + 3) & !3 }
    }

    /// Parses the socket addresses following a routing message: the destination
    /// (`sockaddr_in`), then its link-level address (`sockaddr_dl`).
    fn parse_entry(addresses: &[u8]) -> Option<Device> {
        let in_len = usize::from(*addresses.first()?);
        if i32::from(*addresses.get(1)?) != libc::AF_INET || in_len < 8 || addresses.len() < 8 {
            return None;
        }
        let address = Ipv4Addr::new(addresses[4], addresses[5], addresses[6], addresses[7]);

        let link = addresses.get(round_up(in_len)..)?;
        if link.len() < 8 || i32::from(link[1]) != libc::AF_LINK || link[6] != 6 {
            return None;
        }
        let name_len = usize::from(link[5]);
        let mut octets = [0u8; 6];
        octets.copy_from_slice(link.get(8 + name_len..8 + name_len + 6)?);
        Some(Device {
            address,
            mac: MacAddress(octets),
        })
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn parses_arp_entry() {
            let mut addresses = vec![
                16,
                libc::AF_INET as u8,
                0,
                0,
                192,
                168,
                1,
                35,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
            ];
            addresses.extend_from_slice(&[20, libc::AF_LINK as u8, 4, 0, 6, 3, 6, 0]);
            addresses.extend_from_slice(b"en0");
            addresses.extend_from_slice(&[1, 2, 3, 4, 5, 6, 0, 0, 0]);

            let device = parse_entry(&addresses).unwrap();
            assert_eq!(device.address, Ipv4Addr::new(192, 168, 1, 35));
            assert_eq!(device.mac, MacAddress([1, 2, 3, 4, 5, 6]));
        }
    }
}

#[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
mod platform {
    use std::net::Ipv4Addr;

    use super::{Device, ScanError, Subnet};

    /// Scanning is not supported on this platform.
    pub fn subnets() -> Result<Vec<Subnet>, ScanError> {
        Err(ScanError::Failed)
    }

    /// Scanning is not supported on this platform.
    pub fn resolve(_hosts: &[Ipv4Addr]) -> Result<Vec<Device>, ScanError> {
        Err(ScanError::Failed)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Builds a subnet from dotted addresses.
    fn subnet(address: &str, mask: &str) -> Subnet {
        Subnet {
            address: address.parse().unwrap(),
            mask: mask.parse().unwrap(),
        }
    }

    #[test]
    fn hosts_exclude_network_broadcast_and_self() {
        let hosts = subnet("192.168.1.10", "255.255.255.0").hosts();
        assert_eq!(hosts.len(), 253);
        assert_eq!(hosts.first(), Some(&Ipv4Addr::new(192, 168, 1, 1)));
        assert_eq!(hosts.last(), Some(&Ipv4Addr::new(192, 168, 1, 254)));
        assert!(!hosts.contains(&Ipv4Addr::new(192, 168, 1, 10)));
    }

    #[test]
    fn large_subnets_are_reduced_to_their_24() {
        let hosts = subnet("10.1.2.3", "255.255.0.0").hosts();
        assert_eq!(hosts.len(), 253);
        assert!(hosts.iter().all(|host| host.octets()[..3] == [10, 1, 2]));
        assert_eq!(subnet("10.1.2.3", "255.255.252.0").hosts().len(), 1021);
    }

    #[test]
    fn point_to_point_subnets_have_no_hosts() {
        assert!(subnet("10.0.0.1", "255.255.255.255").hosts().is_empty());
        assert!(subnet("10.0.0.1", "255.255.255.254").hosts().is_empty());
    }

    #[test]
    fn scan_runs_or_reports_no_network() {
        match scan() {
            Ok(_) | Err(ScanError::NoLocalNetwork) => {}
            Err(error) => panic!("{error:?}"),
        }
    }
}
