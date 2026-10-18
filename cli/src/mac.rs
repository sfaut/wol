//! MAC address parsing and formatting.

use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Deserializer, Serialize, Serializer};

/// A 48-bit MAC address.
///
/// Parsed from `AA:BB:CC:DD:EE:FF`, `AA-BB-CC-DD-EE-FF`, `AABB.CCDD.EEFF` or `AABBCCDDEEFF`
/// (case-insensitive), always displayed as `AA:BB:CC:DD:EE:FF`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct MacAddress(pub [u8; 6]);

/// Error returned when a string is not a valid MAC address.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("expected a MAC address such as AA:BB:CC:DD:EE:FF")]
pub struct ParseMacError;

impl MacAddress {
    /// Returns the six bytes of the address.
    pub fn octets(&self) -> [u8; 6] {
        self.0
    }
}

impl FromStr for MacAddress {
    type Err = ParseMacError;

    /// Parses any of the accepted notations, rejecting mixed or misplaced separators.
    fn from_str(input: &str) -> Result<Self, Self::Err> {
        let input = input.trim();
        let bytes = input.as_bytes();

        // Each accepted notation fixes where separators sit; everything else must be hex digits.
        let hex: String = match bytes.len() {
            12 => input.to_owned(),
            14 if bytes[4] == b'.' && bytes[9] == b'.' => input.replace('.', ""),
            17 => {
                let separator = bytes[2];
                if separator != b':' && separator != b'-' {
                    return Err(ParseMacError);
                }
                if (2..17).step_by(3).any(|index| bytes[index] != separator) {
                    return Err(ParseMacError);
                }
                input.replace(separator as char, "")
            }
            _ => return Err(ParseMacError),
        };

        if hex.len() != 12 || !hex.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err(ParseMacError);
        }

        let mut octets = [0u8; 6];
        for (index, octet) in octets.iter_mut().enumerate() {
            *octet = u8::from_str_radix(&hex[index * 2..index * 2 + 2], 16)
                .map_err(|_| ParseMacError)?;
        }

        Ok(Self(octets))
    }
}

impl fmt::Display for MacAddress {
    /// Formats the address as `AA:BB:CC:DD:EE:FF`.
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let [a, b, c, d, e, f] = self.0;
        write!(formatter, "{a:02X}:{b:02X}:{c:02X}:{d:02X}:{e:02X}:{f:02X}")
    }
}

impl Serialize for MacAddress {
    /// Serializes the address in its normalized notation.
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_str(self)
    }
}

impl<'de> Deserialize<'de> for MacAddress {
    /// Deserializes the address from any accepted notation.
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        text.parse()
            .map_err(|_| serde::de::Error::custom(format!("invalid MAC address \"{text}\"")))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const EXPECTED: MacAddress = MacAddress([0xA4, 0xBB, 0x6D, 0x12, 0x34, 0x56]);

    #[test]
    fn parses_every_accepted_notation() {
        for input in [
            "A4:BB:6D:12:34:56",
            "a4-bb-6d-12-34-56",
            "A4BB.6D12.3456",
            "a4bb6d123456",
            " A4:BB:6D:12:34:56 ",
        ] {
            assert_eq!(input.parse::<MacAddress>(), Ok(EXPECTED), "{input}");
        }
    }

    #[test]
    fn rejects_invalid_notations() {
        for input in [
            "",
            "A4:BB:6D:12:34",
            "A4:BB-6D:12:34:56",
            "A4:BB:6D:12:34:5G",
            "A4BB6D12345",
            "A4.BB6D.123456",
            "A4BB:6D12:3456",
        ] {
            assert_eq!(input.parse::<MacAddress>(), Err(ParseMacError), "{input}");
        }
    }

    #[test]
    fn displays_normalized_notation() {
        assert_eq!(EXPECTED.to_string(), "A4:BB:6D:12:34:56");
    }
}
