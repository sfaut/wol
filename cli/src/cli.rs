//! Command-line parsing, command execution and output (human-readable or JSON).

use std::ffi::OsString;
use std::fmt::Write as _;
use std::net::{Ipv4Addr, SocketAddrV4};
use std::path::PathBuf;
use std::process::ExitCode;

use clap::{Args, Parser, Subcommand};
use serde_json::{Value, json};

use crate::config::{self, ConfigFile, Machine};
use crate::error::{EXIT_NETWORK, EXIT_USAGE, Error, Result};
use crate::mac::MacAddress;
use crate::magic;

/// Version of the JSON output schema (see `docs/cli-contract.md`); bump on breaking changes.
pub const SCHEMA: u32 = 1;

/// Wake-on-LAN machine manager: monitor remote machines and wake them up.
#[derive(Debug, Parser)]
#[command(name = "wol", version)]
struct Cli {
    /// Print machine-readable JSON (see docs/cli-contract.md)
    #[arg(long, global = true)]
    json: bool,

    /// Configuration file to use instead of the default lookup
    #[arg(long, global = true, value_name = "PATH")]
    config: Option<PathBuf>,

    /// Command to run.
    #[command(subcommand)]
    command: Command,
}

/// Top-level commands.
#[derive(Debug, Subcommand)]
enum Command {
    /// List groups and machines
    List,

    /// Send magic packets to wake machines up
    Wake {
        /// Names of the machines to wake
        #[arg(required_unless_present = "groups")]
        machines: Vec<String>,

        /// Also wake every machine of this group (repeatable)
        #[arg(long = "group", value_name = "GROUP")]
        groups: Vec<String>,
    },

    /// Add a machine
    Add(AddArgs),

    /// Change a machine
    Edit(EditArgs),

    /// Remove a machine
    Remove {
        /// Name of the machine to remove
        name: String,
    },

    /// Manage groups
    Group {
        /// Group command to run.
        #[command(subcommand)]
        command: GroupCommand,
    },
}

/// Arguments of `wol add`.
#[derive(Debug, Args)]
struct AddArgs {
    /// Unique machine name
    name: String,

    /// MAC address (AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, AABB.CCDD.EEFF or AABBCCDDEEFF)
    #[arg(long)]
    mac: MacAddress,

    /// IP address or host name used for status checks
    #[arg(long)]
    host: String,

    /// Group of the machine
    #[arg(long)]
    group: Option<String>,

    /// Destination address of the magic packet [default: 255.255.255.255]
    #[arg(long, value_name = "IPV4")]
    broadcast: Option<Ipv4Addr>,

    /// UDP port of the magic packet [default: 9]
    #[arg(long)]
    port: Option<u16>,

    /// TCP port tried when ICMP is unavailable (repeatable)
    #[arg(long = "check-port", value_name = "PORT")]
    check_ports: Vec<u16>,
}

/// Arguments of `wol edit`: only the given options change.
#[derive(Debug, Args)]
struct EditArgs {
    /// Name of the machine to change
    name: String,

    /// New machine name
    #[arg(long = "name", value_name = "NAME")]
    new_name: Option<String>,

    /// New MAC address
    #[arg(long)]
    mac: Option<MacAddress>,

    /// New IP address or host name
    #[arg(long)]
    host: Option<String>,

    /// New group
    #[arg(long, conflicts_with = "no_group")]
    group: Option<String>,

    /// Remove the machine from its group
    #[arg(long)]
    no_group: bool,

    /// New destination address of the magic packet
    #[arg(long, value_name = "IPV4", conflicts_with = "no_broadcast")]
    broadcast: Option<Ipv4Addr>,

    /// Go back to the default destination address
    #[arg(long)]
    no_broadcast: bool,

    /// New UDP port of the magic packet
    #[arg(long, conflicts_with = "no_port")]
    port: Option<u16>,

    /// Go back to the default UDP port
    #[arg(long)]
    no_port: bool,

    /// Replace the TCP check ports (repeatable)
    #[arg(
        long = "check-port",
        value_name = "PORT",
        conflicts_with = "no_check_ports"
    )]
    check_ports: Vec<u16>,

    /// Remove all TCP check ports
    #[arg(long)]
    no_check_ports: bool,
}

/// Group commands.
#[derive(Debug, Subcommand)]
enum GroupCommand {
    /// Add a group at the end of the list
    Add {
        /// Unique group name
        name: String,
    },

    /// Rename a group (its machines follow)
    Rename {
        /// Current name
        name: String,

        /// New name
        new_name: String,
    },

    /// Remove a group
    Remove {
        /// Name of the group to remove
        name: String,

        /// Keep the group's machines, without a group
        #[arg(long)]
        ungroup: bool,
    },

    /// Move a group to another position in the list
    Move {
        /// Name of the group to move
        name: String,

        /// New 1-based position
        position: usize,
    },
}

/// Result of a successful command: what to print in each output mode, and the exit code.
struct Outcome {
    /// JSON document (without the `schema` field, added when printing).
    json: Value,

    /// Human-readable text.
    text: String,

    /// Process exit code (non-zero for partial failures).
    exit: u8,
}

impl Outcome {
    /// Builds a fully successful outcome.
    fn ok(json: Value, text: String) -> Self {
        Self {
            json,
            text,
            exit: 0,
        }
    }
}

/// Parses `args`, runs the command and prints its result.
pub fn run<I>(args: I) -> ExitCode
where
    I: IntoIterator,
    I::Item: Into<OsString> + Clone,
{
    let args: Vec<OsString> = args.into_iter().map(Into::into).collect();

    let cli = match Cli::try_parse_from(&args) {
        Ok(cli) => cli,
        Err(error) if !error.use_stderr() => {
            // --help and --version.
            let _ = error.print();
            return ExitCode::SUCCESS;
        }
        Err(error) => {
            if args.iter().any(|arg| arg == "--json") {
                let message = error.render().to_string();
                let message = message.trim().trim_start_matches("error: ");
                print_json_error("invalid_usage", message, json!({}));
            } else {
                let _ = error.print();
            }
            return ExitCode::from(EXIT_USAGE);
        }
    };

    match execute(&cli) {
        Ok(outcome) => {
            if cli.json {
                println!("{}", with_schema(outcome.json));
            } else if !outcome.text.is_empty() {
                print!("{}", outcome.text);
            }
            ExitCode::from(outcome.exit)
        }
        Err(error) => {
            if cli.json {
                print_json_error(error.code(), &error.to_string(), error.details());
            } else {
                eprintln!("error: {error}");
            }
            ExitCode::from(error.exit_code())
        }
    }
}

/// Adds the `schema` field at the beginning of a JSON object.
fn with_schema(document: Value) -> Value {
    let mut object = serde_json::Map::new();
    object.insert("schema".to_owned(), json!(SCHEMA));
    if let Value::Object(fields) = document {
        object.extend(fields);
    }
    Value::Object(object)
}

/// Prints a JSON error document on `stderr`.
fn print_json_error(code: &str, message: &str, details: Value) {
    eprintln!(
        "{}",
        with_schema(json!({ "error": { "code": code, "message": message, "details": details } }))
    );
}

/// Runs the parsed command.
///
/// # Errors
///
/// Returns the error to report; partial failures are reported through [`Outcome::exit`].
fn execute(cli: &Cli) -> Result<Outcome> {
    let mut file = ConfigFile::load(config::locate(cli.config.as_deref()))?;

    match &cli.command {
        Command::List => Ok(list(&file)),
        Command::Wake { machines, groups } => wake(&file, machines, groups),
        Command::Add(args) => {
            let machine = Machine {
                name: args.name.clone(),
                group: args.group.clone(),
                mac: args.mac,
                host: args.host.clone(),
                broadcast: args.broadcast,
                port: args.port,
                check_ports: args.check_ports.clone(),
            };
            let machine = file.add_machine(&machine)?;
            file.save()?;
            Ok(Outcome::ok(
                json!({ "machine": machine }),
                format!("Added machine \"{}\".\n", machine.name),
            ))
        }
        Command::Edit(args) => {
            let index = file
                .config()
                .machine_index(&args.name)
                .ok_or_else(|| Error::MachineNotFound(args.name.clone()))?;
            let current = &file.config().machines[index];
            let machine = Machine {
                name: args
                    .new_name
                    .clone()
                    .unwrap_or_else(|| current.name.clone()),
                group: if args.no_group {
                    None
                } else {
                    args.group.clone().or_else(|| current.group.clone())
                },
                mac: args.mac.unwrap_or(current.mac),
                host: args.host.clone().unwrap_or_else(|| current.host.clone()),
                broadcast: if args.no_broadcast {
                    None
                } else {
                    args.broadcast.or(current.broadcast)
                },
                port: if args.no_port {
                    None
                } else {
                    args.port.or(current.port)
                },
                check_ports: match (args.no_check_ports, args.check_ports.is_empty()) {
                    (true, _) => Vec::new(),
                    (false, true) => current.check_ports.clone(),
                    (false, false) => args.check_ports.clone(),
                },
            };
            let machine = file.edit_machine(&args.name, &machine)?;
            file.save()?;
            Ok(Outcome::ok(
                json!({ "machine": machine }),
                format!("Updated machine \"{}\".\n", machine.name),
            ))
        }
        Command::Remove { name } => {
            let machine = file.remove_machine(name)?;
            file.save()?;
            Ok(Outcome::ok(
                json!({ "machine": machine }),
                format!("Removed machine \"{}\".\n", machine.name),
            ))
        }
        Command::Group { command } => group(&mut file, command),
    }
}

/// Runs a group command.
///
/// # Errors
///
/// Returns the configuration error to report.
fn group(file: &mut ConfigFile, command: &GroupCommand) -> Result<Outcome> {
    let outcome = match command {
        GroupCommand::Add { name } => {
            let group = file.add_group(name)?;
            let position = file.config().groups.len();
            Outcome::ok(
                json!({ "group": group, "position": position }),
                format!("Added group \"{}\".\n", group.name),
            )
        }
        GroupCommand::Rename { name, new_name } => {
            let group = file.rename_group(name, new_name)?;
            Outcome::ok(
                json!({ "group": group }),
                format!("Renamed group \"{name}\" to \"{}\".\n", group.name),
            )
        }
        GroupCommand::Remove { name, ungroup } => {
            let (group, ungrouped) = file.remove_group(name, *ungroup)?;
            let mut text = format!("Removed group \"{}\".\n", group.name);
            if !ungrouped.is_empty() {
                let _ = writeln!(text, "Now without a group: {}.", ungrouped.join(", "));
            }
            Outcome::ok(json!({ "group": group, "ungrouped": ungrouped }), text)
        }
        GroupCommand::Move { name, position } => {
            let (group, position) = file.move_group(name, *position)?;
            Outcome::ok(
                json!({ "group": group, "position": position }),
                format!("Moved group \"{}\" to position {position}.\n", group.name),
            )
        }
    };

    file.save()?;
    Ok(outcome)
}

/// Lists groups and machines: groups in their order, then machines without a group.
fn list(file: &ConfigFile) -> Outcome {
    let config = file.config();
    let json = json!({
        "config": file.path(),
        "groups": config.groups,
        "machines": config.machines,
    });

    if config.groups.is_empty() && config.machines.is_empty() {
        return Outcome::ok(
            json,
            "No machines yet. Add one with: wol add <NAME> --mac <MAC> --host <HOST>\n".to_owned(),
        );
    }

    let width = config
        .machines
        .iter()
        .map(|machine| machine.name.chars().count())
        .max()
        .unwrap_or(0);
    let host_width = config
        .machines
        .iter()
        .map(|machine| machine.host.chars().count())
        .max()
        .unwrap_or(0);
    let line = |machine: &Machine| {
        format!(
            "  {:<width$}  {}  {:<host_width$}\n",
            machine.name, machine.mac, machine.host
        )
    };

    let mut text = String::new();
    for group in &config.groups {
        let _ = writeln!(text, "{}", group.name);
        let mut members = config.group_machines(&group.name).peekable();
        if members.peek().is_none() {
            text.push_str("  (no machines)\n");
        }
        members.for_each(|machine| text.push_str(&line(machine)));
    }

    let ungrouped: Vec<&Machine> = config
        .machines
        .iter()
        .filter(|machine| machine.group.is_none())
        .collect();
    if !ungrouped.is_empty() {
        if !config.groups.is_empty() {
            text.push_str("Ungrouped\n");
        }
        ungrouped
            .into_iter()
            .for_each(|machine| text.push_str(&line(machine)));
    }

    // Trailing spaces come from padding the last column.
    let text = text
        .lines()
        .map(str::trim_end)
        .collect::<Vec<_>>()
        .join("\n")
        + "\n";
    Outcome::ok(json, text)
}

/// Sends magic packets to the named machines and to every machine of the named groups.
///
/// # Errors
///
/// Returns [`Error::MachineNotFound`] or [`Error::GroupNotFound`] before sending anything;
/// send failures are reported per machine, with exit code [`EXIT_NETWORK`].
fn wake(file: &ConfigFile, names: &[String], groups: &[String]) -> Result<Outcome> {
    let config = file.config();

    let mut targets: Vec<&Machine> = Vec::new();
    for name in names {
        let index = config
            .machine_index(name)
            .ok_or_else(|| Error::MachineNotFound(name.clone()))?;
        targets.push(&config.machines[index]);
    }
    for group in groups {
        let group = config
            .group(group)
            .ok_or_else(|| Error::GroupNotFound(group.clone()))?;
        targets.extend(config.group_machines(&group.name));
    }
    let mut seen = Vec::new();
    targets.retain(|machine| {
        let new = !seen.contains(&machine.name);
        seen.push(machine.name.clone());
        new
    });

    let mut results = Vec::new();
    let mut text = String::new();
    let mut exit = 0;
    for machine in targets {
        let target = SocketAddrV4::new(
            machine.broadcast.unwrap_or(magic::DEFAULT_BROADCAST),
            machine.port.unwrap_or(magic::DEFAULT_PORT),
        );
        match magic::send(machine.mac, target) {
            Ok(()) => {
                results.push(json!({ "machine": machine.name, "target": target.to_string(), "status": "sent" }));
                let _ = writeln!(
                    text,
                    "Magic packet sent to \"{}\" ({target}).",
                    machine.name
                );
            }
            Err(error) => {
                exit = EXIT_NETWORK;
                results.push(json!({
                    "machine": machine.name,
                    "target": target.to_string(),
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
