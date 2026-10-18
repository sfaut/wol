//! Command-line parsing, command execution and output (human-readable or JSON).

use std::ffi::OsString;
use std::fmt::Write as _;
use std::net::{Ipv4Addr, SocketAddrV4};
use std::path::PathBuf;
use std::process::ExitCode;

use clap::{Args, Parser, Subcommand};
use serde_json::{Value, json};

use crate::config::{self, Config, ConfigFile, ConfigStatus, Interface, Machine, SharedMac};
use crate::error::{EXIT_NETWORK, EXIT_USAGE, Error, Result};
use crate::mac::MacAddress;
use crate::probe::{self, Probe, Reason, State};
use crate::{magic, scan, time, watch};

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

    /// Check whether machines are online (all machines by default)
    Status {
        /// Machines to check.
        #[command(flatten)]
        selection: Selection,
    },

    /// Monitor machines and report every state change (all machines by default)
    Watch {
        /// Machines to monitor.
        #[command(flatten)]
        selection: Selection,

        /// Time between two checks, in seconds
        #[arg(long, value_name = "SECONDS", default_value_t = 10, value_parser = clap::value_parser!(u64).range(1..=3_600))]
        interval: u64,
    },

    /// List the devices found on the local network
    Scan,

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

/// Machines targeted by `status` and `watch`: every machine when nothing is given.
#[derive(Debug, Args)]
struct Selection {
    /// Names of the machines
    machines: Vec<String>,

    /// Also include every machine of this group (repeatable)
    #[arg(long = "group", value_name = "GROUP")]
    groups: Vec<String>,
}

/// Arguments of `wol add`.
#[derive(Debug, Args)]
struct AddArgs {
    /// Unique machine name
    name: String,

    /// Network interface: MAC address, optionally followed by =LABEL (repeatable)
    #[arg(long = "mac", value_name = "MAC[=LABEL]", required = true, value_parser = parse_interface)]
    interfaces: Vec<Interface>,

    /// Group of the machine
    #[arg(long)]
    group: Option<String>,

    /// Destination address of the magic packet [default: 255.255.255.255]
    #[arg(long, value_name = "IPV4")]
    broadcast: Option<Ipv4Addr>,

    /// UDP port of the magic packet [default: 9]
    #[arg(long)]
    port: Option<u16>,
}

/// Arguments of `wol edit`: only the given options change.
#[derive(Debug, Args)]
struct EditArgs {
    /// Name of the machine to change
    name: String,

    /// New machine name
    #[arg(long = "name", value_name = "NAME")]
    new_name: Option<String>,

    /// Replace all network interfaces (repeatable)
    #[arg(long = "mac", value_name = "MAC[=LABEL]", value_parser = parse_interface, conflicts_with_all = ["add_interfaces", "remove_interfaces"])]
    interfaces: Vec<Interface>,

    /// Add a network interface, or change the label of an existing one (repeatable)
    #[arg(long = "add-mac", value_name = "MAC[=LABEL]", value_parser = parse_interface)]
    add_interfaces: Vec<Interface>,

    /// Remove the network interface with this MAC address (repeatable)
    #[arg(long = "remove-mac", value_name = "MAC")]
    remove_interfaces: Vec<MacAddress>,

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
}

/// Parses a `MAC[=LABEL]` argument; the label is checked with the rest of the machine.
fn parse_interface(text: &str) -> std::result::Result<Interface, String> {
    let (mac, label) = match text.split_once('=') {
        Some((mac, label)) => (mac, Some(label.to_owned())),
        None => (text, None),
    };
    Ok(Interface {
        mac: mac.parse().map_err(|error| format!("{error}"))?,
        label,
    })
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
pub struct Outcome {
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

    let mut file = match ConfigFile::load(config::locate(cli.config.as_deref())) {
        Ok(file) => file,
        Err(error) => {
            // The error itself names the file (invalid or unreadable).
            report_error(cli.json, &error, None);
            return ExitCode::from(error.exit_code());
        }
    };

    let result = execute(&cli, &mut file);
    let config = json!({ "path": file.path(), "status": file.status() });

    match result {
        Ok(outcome) => {
            if cli.json {
                println!("{}", document(Some(config), outcome.json));
            } else {
                print!("{}", outcome.text);
                eprintln!("{}", config_line(&file));
            }
            ExitCode::from(outcome.exit)
        }
        Err(error) => {
            report_error(cli.json, &error, Some(&file));
            ExitCode::from(error.exit_code())
        }
    }
}

/// Builds a JSON document: `schema` first, then `config` when known, then the `body` fields.
pub(crate) fn document(config: Option<Value>, body: Value) -> Value {
    let mut object = serde_json::Map::new();
    object.insert("schema".to_owned(), json!(SCHEMA));
    if let Some(config) = config {
        object.insert("config".to_owned(), config);
    }
    if let Value::Object(fields) = body {
        object.extend(fields);
    }
    Value::Object(object)
}

/// Describes, for humans, which configuration file was used and what happened to it.
fn config_line(file: &ConfigFile) -> String {
    let path = file.path().display();
    match file.status() {
        ConfigStatus::Loaded => format!("Configuration: {path}"),
        ConfigStatus::Updated => format!("Configuration: {path} (updated)"),
        ConfigStatus::Created => format!("Configuration: {path} (created)"),
        ConfigStatus::Missing => format!("Configuration: {path} (not created yet)"),
    }
}

/// Reports an error on `stderr`, with the configuration file when it was loaded.
fn report_error(json: bool, error: &Error, file: Option<&ConfigFile>) {
    if json {
        let config = file.map(|file| json!({ "path": file.path(), "status": file.status() }));
        let body = json!({ "error": { "code": error.code(), "message": error.to_string(), "details": error.details() } });
        eprintln!("{}", document(config, body));
    } else {
        eprintln!("error: {error}");
        if let Some(file) = file {
            eprintln!("{}", config_line(file));
        }
    }
}

/// Prints a JSON error document on `stderr`, for errors raised before any configuration is loaded.
fn print_json_error(code: &str, message: &str, details: Value) {
    let body = json!({ "error": { "code": code, "message": message, "details": details } });
    eprintln!("{}", document(None, body));
}

/// Runs the parsed command against the loaded configuration file.
///
/// # Errors
///
/// Returns the error to report; partial failures are reported through [`Outcome::exit`].
fn execute(cli: &Cli, file: &mut ConfigFile) -> Result<Outcome> {
    match &cli.command {
        Command::List => Ok(list(file)),
        Command::Wake { machines, groups } => wake(file, machines, groups),
        Command::Scan => scan_network(file),
        Command::Status { selection } => status(file, selection),
        Command::Watch {
            selection,
            interval,
        } => watch::watch(
            file,
            &watch::Options {
                names: &selection.machines,
                groups: &selection.groups,
                interval: std::time::Duration::from_secs(*interval),
                json: cli.json,
            },
        ),
        Command::Add(args) => {
            let machine = Machine {
                name: args.name.clone(),
                group: args.group.clone(),
                interfaces: args.interfaces.clone(),
                broadcast: args.broadcast,
                port: args.port,
            };
            let machine = file.add_machine(&machine)?;
            file.save()?;
            Ok(machine_outcome(
                file,
                &machine,
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
                interfaces: edited_interfaces(current, args)?,
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
            };
            let machine = file.edit_machine(&args.name, &machine)?;
            file.save()?;
            Ok(machine_outcome(
                file,
                &machine,
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
        Command::Group { command } => group(file, command),
    }
}

/// Applies the interface options of `wol edit` to the interfaces of `current`.
///
/// # Errors
///
/// Returns [`Error::InvalidValue`] when `--remove-mac` names a MAC address the machine does
/// not have.
fn edited_interfaces(current: &Machine, args: &EditArgs) -> Result<Vec<Interface>> {
    if !args.interfaces.is_empty() {
        return Ok(args.interfaces.clone());
    }

    let mut interfaces = current.interfaces.clone();
    for mac in &args.remove_interfaces {
        let index = interfaces
            .iter()
            .position(|interface| interface.mac == *mac)
            .ok_or_else(|| Error::InvalidValue {
                field: "remove-mac",
                reason: format!("machine \"{}\" has no interface {mac}", current.name),
            })?;
        interfaces.remove(index);
    }
    for added in &args.add_interfaces {
        match interfaces
            .iter_mut()
            .find(|interface| interface.mac == added.mac)
        {
            Some(interface) => interface.label.clone_from(&added.label),
            None => interfaces.push(added.clone()),
        }
    }
    Ok(interfaces)
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
        "groups": config.groups,
        "machines": config.machines,
        "warnings": warnings_json(&config.shared_macs()),
    });

    if config.groups.is_empty() && config.machines.is_empty() {
        return Outcome::ok(
            json,
            "No machines yet. Add one with: wol add <NAME> --mac <MAC>\n".to_owned(),
        );
    }

    let width = config
        .machines
        .iter()
        .map(|machine| machine.name.chars().count())
        .max()
        .unwrap_or(0);
    // One line per interface; the machine name only on the first one.
    let line = |machine: &Machine| {
        let mut lines = String::new();
        for (index, interface) in machine.interfaces.iter().enumerate() {
            let name = if index == 0 {
                machine.name.as_str()
            } else {
                ""
            };
            let label = interface.label.as_deref().unwrap_or_default();
            let _ = writeln!(lines, "  {name:<width$}  {}  {label}", interface.mac);
        }
        lines
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
        + "\n"
        + &warnings_text(&config.shared_macs());
    Outcome::ok(json, text)
}

/// JSON warnings about MAC addresses shared by several machines.
fn warnings_json(shared: &[SharedMac]) -> Vec<Value> {
    shared
        .iter()
        .map(|entry| {
            json!({
                "code": "shared_mac",
                "message": shared_mac_message(entry),
                "mac": entry.mac,
                "machines": entry.machines,
            })
        })
        .collect()
}

/// Human-readable warnings about MAC addresses shared by several machines.
fn warnings_text(shared: &[SharedMac]) -> String {
    shared
        .iter()
        .map(|entry| format!("Warning: {}.\n", shared_mac_message(entry)))
        .collect()
}

/// Describes a shared MAC address.
fn shared_mac_message(entry: &SharedMac) -> String {
    let machines: Vec<String> = entry
        .machines
        .iter()
        .map(|name| format!("\"{name}\""))
        .collect();
    format!(
        "MAC address {} is used by several machines ({}), which cannot be told apart when it answers",
        entry.mac,
        machines.join(", ")
    )
}

/// Builds the outcome of `add` / `edit`, warning about the MAC addresses `machine` shares
/// with other machines.
fn machine_outcome(file: &ConfigFile, machine: &Machine, text: String) -> Outcome {
    let shared: Vec<SharedMac> = file
        .config()
        .shared_macs()
        .into_iter()
        .filter(|entry| entry.machines.contains(&machine.name))
        .collect();
    Outcome::ok(
        json!({ "machine": machine, "warnings": warnings_json(&shared) }),
        text + &warnings_text(&shared),
    )
}

/// Lists the devices found on the local network, with the machines that use their MAC address.
///
/// # Errors
///
/// Returns [`Error::Scan`] when the local network cannot be scanned.
fn scan_network(file: &ConfigFile) -> Result<Outcome> {
    let devices = scan::scan().map_err(|error| Error::Scan(error.into()))?;
    let config = file.config();

    // Machines (and the label of their interface) using a MAC address.
    let users = |mac: MacAddress| -> Vec<(&str, Option<&str>)> {
        config
            .machines
            .iter()
            .filter_map(|machine| {
                machine
                    .interfaces
                    .iter()
                    .find(|interface| interface.mac == mac)
                    .map(|interface| (machine.name.as_str(), interface.label.as_deref()))
            })
            .collect()
    };

    let json = json!({
        "scanned_at": time::now(),
        "devices": devices.iter().map(|device| json!({
            "address": device.address.to_string(),
            "mac": device.mac,
            "machines": users(device.mac).into_iter().map(|(machine, label)| json!({ "machine": machine, "label": label })).collect::<Vec<_>>(),
        })).collect::<Vec<_>>(),
    });

    if devices.is_empty() {
        return Ok(Outcome::ok(json, "No devices found.\n".to_owned()));
    }
    let mut text = String::new();
    for device in &devices {
        let known: Vec<String> = users(device.mac)
            .into_iter()
            .map(|(machine, label)| match label {
                Some(label) => format!("{machine} ({label})"),
                None => machine.to_owned(),
            })
            .collect();
        let _ = writeln!(
            text,
            "{:<15}  {}  {}",
            device.address.to_string(),
            device.mac,
            known.join(", ")
        );
    }
    let text = text
        .lines()
        .map(str::trim_end)
        .collect::<Vec<_>>()
        .join("\n")
        + "\n";
    Ok(Outcome::ok(json, text))
}

/// Checks the selected machines once, with one scan of the local network.
///
/// # Errors
///
/// Returns [`Error::MachineNotFound`] or [`Error::GroupNotFound`]; a failed scan is reported
/// as machine states, with exit code [`EXIT_NETWORK`].
fn status(file: &ConfigFile, selection: &Selection) -> Result<Outcome> {
    let config = file.config();
    let machines = config.select(&selection.machines, &selection.groups)?;

    let scan = scan::scan();
    let probes = probe::probe_all(&machines, &scan);
    let json = json!({
        "checked_at": time::now(),
        "machines": machines.iter().zip(&probes).map(|(machine, probe)| probe_json(machine, probe)).collect::<Vec<_>>(),
    });

    Ok(Outcome {
        json,
        text: status_text(config, &machines, &probes),
        exit: if scan.is_err() { EXIT_NETWORK } else { 0 },
    })
}

/// Describes the state of machines for humans, by group (groups without selected machines are skipped).
pub(crate) fn status_text(config: &Config, machines: &[&Machine], probes: &[Probe]) -> String {
    if machines.is_empty() {
        return "No machines to check.\n".to_owned();
    }

    let width = machines
        .iter()
        .map(|machine| machine.name.chars().count())
        .max()
        .unwrap_or(0);
    let mut text = String::new();
    let mut section = |title: Option<&str>, filter: &dyn Fn(&Machine) -> bool| {
        let mut rows = machines
            .iter()
            .zip(probes)
            .filter(|(machine, _)| filter(machine))
            .peekable();
        if rows.peek().is_none() {
            return;
        }
        if let Some(title) = title {
            let _ = writeln!(text, "{title}");
        }
        for (machine, probe) in rows {
            let _ = writeln!(text, "  {:<width$}  {}", machine.name, probe_text(probe));
        }
    };

    for group in &config.groups {
        section(Some(&group.name), &|machine| {
            machine.group.as_deref() == Some(group.name.as_str())
        });
    }
    let title = (!config.groups.is_empty()).then_some("Ungrouped");
    section(title, &|machine| machine.group.is_none());

    text
}

/// JSON entry describing the state of a machine.
pub(crate) fn probe_json(machine: &Machine, probe: &Probe) -> Value {
    json!({
        "machine": machine.name,
        "state": probe.state,
        "mac": probe.interface.as_ref().map(|interface| interface.mac),
        "label": probe.interface.as_ref().and_then(|interface| interface.label.as_deref()),
        "address": probe.address.map(|address| address.to_string()),
        "reason": probe.reason,
    })
}

/// Short human-readable description of a state, e.g. `online   192.168.1.20 (Wi-Fi)` or `offline`.
pub(crate) fn probe_text(probe: &Probe) -> String {
    match (probe.state, probe.address) {
        (State::Online, Some(address)) => match probe
            .interface
            .as_ref()
            .and_then(|interface| interface.label.as_deref())
        {
            Some(label) => format!("online   {address} ({label})"),
            None => format!("online   {address}"),
        },
        (State::Error, _) => format!(
            "error    {}",
            probe.reason.map(Reason::message).unwrap_or_default()
        ),
        (state, _) => state.code().to_owned(),
    }
}

/// Sends magic packets to every interface of the named machines and of every machine of the
/// named groups.
///
/// # Errors
///
/// Returns [`Error::MachineNotFound`] or [`Error::GroupNotFound`] before sending anything;
/// send failures are reported per machine, with exit code [`EXIT_NETWORK`].
fn wake(file: &ConfigFile, names: &[String], groups: &[String]) -> Result<Outcome> {
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
