//! Errors reported by the CLI, with their stable codes and exit codes (see `docs/cli-contract.md`).

use std::io;
use std::path::PathBuf;

use serde_json::{Value, json};

use crate::probe::Reason;

/// Exit code: generic failure.
pub const EXIT_FAILURE: u8 = 1;

/// Exit code: invalid usage (bad arguments or values).
pub const EXIT_USAGE: u8 = 2;

/// Exit code: configuration file invalid.
pub const EXIT_CONFIG: u8 = 3;

/// Exit code: machine or group not found.
pub const EXIT_NOT_FOUND: u8 = 4;

/// Exit code: network error.
pub const EXIT_NETWORK: u8 = 5;

/// Any error the CLI can report.
#[derive(Debug, thiserror::Error)]
pub enum Error {
    /// The configuration file (or its directory) cannot be read or written.
    #[error("cannot access {}: {source}", path.display())]
    Io {
        /// File or directory involved.
        path: PathBuf,
        /// Underlying I/O error.
        source: io::Error,
    },

    /// The configuration file is not valid TOML or breaks a rule of the contract.
    #[error("invalid configuration file {}{}: {message}", path.display(), line.map(|line| format!(" (line {line})")).unwrap_or_default())]
    ConfigInvalid {
        /// Configuration file path.
        path: PathBuf,
        /// 1-based line of the error, when known.
        line: Option<usize>,
        /// Description of the problem.
        message: String,
    },

    /// A value given on the command line is invalid.
    #[error("invalid value for {field}: {reason}")]
    InvalidValue {
        /// Name of the field or argument.
        field: &'static str,
        /// Why the value was rejected.
        reason: String,
    },

    /// No machine has this name.
    #[error("machine not found: {0}")]
    MachineNotFound(String),

    /// No group has this name.
    #[error("group not found: {0}")]
    GroupNotFound(String),

    /// A machine with this name already exists.
    #[error("a machine named \"{0}\" already exists")]
    MachineAlreadyExists(String),

    /// A group with this name already exists.
    #[error("a group named \"{0}\" already exists")]
    GroupAlreadyExists(String),

    /// The group still contains machines.
    #[error("group \"{name}\" still contains {} machine(s); use --ungroup to keep them without a group", machines.len())]
    GroupNotEmpty {
        /// Group name.
        name: String,
        /// Machines belonging to the group.
        machines: Vec<String>,
    },

    /// The local network cannot be scanned.
    #[error("{}", .0.message())]
    Scan(Reason),
}

impl Error {
    /// Stable, language-neutral code identifying the error in JSON output.
    pub fn code(&self) -> &'static str {
        match self {
            Self::Io { .. } => "io_error",
            Self::ConfigInvalid { .. } => "config_invalid",
            Self::InvalidValue { .. } => "invalid_value",
            Self::MachineNotFound(_) => "machine_not_found",
            Self::GroupNotFound(_) => "group_not_found",
            Self::MachineAlreadyExists(_) => "machine_already_exists",
            Self::GroupAlreadyExists(_) => "group_already_exists",
            Self::GroupNotEmpty { .. } => "group_not_empty",
            Self::Scan(Reason::NoLocalNetwork) => "no_local_network",
            Self::Scan(Reason::ScanFailed) => "scan_failed",
        }
    }

    /// Process exit code associated with the error.
    pub fn exit_code(&self) -> u8 {
        match self {
            Self::Io { .. } => EXIT_FAILURE,
            Self::ConfigInvalid { .. } => EXIT_CONFIG,
            Self::InvalidValue { .. }
            | Self::MachineAlreadyExists(_)
            | Self::GroupAlreadyExists(_)
            | Self::GroupNotEmpty { .. } => EXIT_USAGE,
            Self::MachineNotFound(_) | Self::GroupNotFound(_) => EXIT_NOT_FOUND,
            Self::Scan(_) => EXIT_NETWORK,
        }
    }

    /// Structured details for front-ends, so they never have to parse `message`.
    pub fn details(&self) -> Value {
        match self {
            Self::Io { path, .. } => json!({ "path": path }),
            Self::ConfigInvalid { path, line, .. } => json!({ "path": path, "line": line }),
            Self::InvalidValue { field, .. } => json!({ "field": field }),
            Self::MachineNotFound(name) | Self::MachineAlreadyExists(name) => {
                json!({ "machine": name })
            }
            Self::GroupNotFound(name) | Self::GroupAlreadyExists(name) => json!({ "group": name }),
            Self::GroupNotEmpty { name, machines } => {
                json!({ "group": name, "machines": machines })
            }
            Self::Scan(_) => json!({}),
        }
    }
}

/// Result type of the CLI.
pub type Result<T> = std::result::Result<T, Error>;
