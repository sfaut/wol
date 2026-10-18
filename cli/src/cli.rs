//! Command-line parsing, command execution and output (human-readable or JSON).

use std::ffi::OsString;
use std::fmt::Write as _;
use std::io::{self, Write as _};
use std::net::Ipv4Addr;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

use clap::{Args, CommandFactory, Parser, Subcommand};
use serde_json::{Value, json};

use crate::config::{self, Config, ConfigFile, Interface, Machine, SharedMac};
use crate::error::{EXIT_NETWORK, EXIT_USAGE, Error, Result};
use crate::mac::MacAddress;
use crate::probe::{self, Probe, Reason, State};
use crate::{scan, time, wake, watch};

/// Version of the JSON output schema (see `docs/cli-contract.md`); bump on breaking changes.
pub const SCHEMA: u32 = 1;

/// Wake-on-LAN machine manager: monitor machines of the local network and wake them up.
///
/// Machines are described in a wol.toml file, by their network interfaces (MAC addresses).
/// Wol finds them by scanning the local network, which also gives their current IP address,
/// and wakes them up with Wake-on-LAN magic packets.
///
/// Use -h for a summary, --help for details.
#[derive(Debug, Parser)]
#[command(name = "wol", bin_name = "wol", version, max_term_width = 80)]
struct Cli {
    /// Print machine-readable JSON
    ///
    /// Every command then prints one JSON document on stdout (errors: on stderr), or one JSON
    /// line per event for `watch`. The format is described in docs/cli-contract.md.
    #[arg(long, global = true, display_order = 100)]
    json: bool,

    /// Use this configuration file
    ///
    /// The file is created on the first change if it does not exist. Without this option, wol
    /// uses wol.toml next to the executable when present (portable mode), otherwise wol.toml in
    /// the user configuration directory (e.g. %APPDATA%\wol on Windows, ~/.config/wol on Linux).
    #[arg(long, global = true, value_name = "PATH", display_order = 101)]
    config: Option<PathBuf>,

    /// Command to run; without one, the help is printed with the configuration file in use.
    #[command(subcommand)]
    command: Option<Command>,
}

/// Top-level commands.
#[derive(Debug, Subcommand)]
enum Command {
    /// List groups and machines, with their network interfaces
    ///
    /// Groups come in their order, then machines without a group. MAC addresses used by several
    /// machines are reported as warnings.
    List,

    /// Wake machines up
    ///
    /// Sends magic packets to every network interface of the machines. The machines must have
    /// Wake-on-LAN enabled (firmware and network adapter settings).
    Wake {
        /// Names of the machines to wake (case-insensitive)
        #[arg(required_unless_present = "groups")]
        machines: Vec<String>,

        /// Also wake every machine of this group (repeatable)
        #[arg(long = "group", value_name = "GROUP")]
        groups: Vec<String>,

        /// Wait until the machines are online, up to SECONDS [default: 120]
        ///
        /// Scans the local network every 2 seconds and reports each machine as it comes
        /// online. Machines still offline after SECONDS are reported as timed out (exit
        /// code 6). The value needs an equals sign: --wait=300.
        #[arg(long, value_name = "SECONDS", num_args = 0..=1, require_equals = true, default_missing_value = "120", value_parser = clap::value_parser!(u64).range(1..=3_600))]
        wait: Option<u64>,
    },

    /// Check whether machines are online (all machines by default)
    ///
    /// Scans the local network once: a machine is online when any of its MAC addresses
    /// answers, and its current IP address is shown.
    Status {
        /// Machines to check.
        #[command(flatten)]
        selection: Selection,
    },

    /// Monitor machines and report every state change (all machines by default)
    ///
    /// Prints the state of every machine, then one line per change, until stopped (Ctrl+C).
    /// Changes made to the configuration file meanwhile are taken into account.
    Watch {
        /// Machines to monitor.
        #[command(flatten)]
        selection: Selection,

        /// Time between two scans, in seconds (1 to 3600)
        ///
        /// A machine that stops answering is reported offline only after two scans in a row,
        /// so that a single missed answer does not count.
        #[arg(long, value_name = "SECONDS", default_value_t = 10, value_parser = clap::value_parser!(u64).range(1..=3_600))]
        interval: u64,
    },

    /// List the devices found on the local network
    ///
    /// Shows the IP and MAC address of every device that answers, and the machines using
    /// each MAC address. Handy to find the MAC address of a machine to add.
    Scan,

    /// Add a machine
    #[command(
        after_help = "Examples:\n  wol add NAS --mac 00:11:32:BC:72:E9\n  wol add \"Office PC\" --mac A4BB6D123456=Ethernet --mac A4BB6D123457=Wi-Fi"
    )]
    Add(AddArgs),

    /// Change a machine (only the given options change)
    Edit(EditArgs),

    /// Remove a machine
    Remove {
        /// Name of the machine to remove (case-insensitive)
        name: String,
    },

    /// Print the version of wol and of its JSON output
    ///
    /// Front-ends use `wol version --json` to check that they understand this CLI: the
    /// `schema` field gives the version of the JSON output. The configuration file is not
    /// read.
    Version,

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
    /// Names of the machines (case-insensitive); all machines when none is given
    machines: Vec<String>,

    /// Also include every machine of this group (repeatable)
    #[arg(long = "group", value_name = "GROUP")]
    groups: Vec<String>,
}

/// Arguments of `wol add`.
#[derive(Debug, Args)]
struct AddArgs {
    /// Unique machine name (case-insensitive, up to 64 characters)
    name: String,

    /// Network interface of the machine (repeatable, at least one)
    ///
    /// MAC address, optionally followed by =LABEL to tell interfaces apart, e.g.
    /// A4:BB:6D:12:34:56=Ethernet. Accepted forms: AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF,
    /// AABB.CCDD.EEFF and AABBCCDDEEFF. A MAC address already used by another machine is
    /// accepted with a warning.
    #[arg(long = "mac", value_name = "MAC[=LABEL]", required = true, value_parser = parse_interface)]
    interfaces: Vec<Interface>,

    /// Group of the machine (it must exist: see `wol group add`)
    #[arg(long)]
    group: Option<String>,

    /// Destination address of the magic packets [default: 255.255.255.255]
    ///
    /// The default reaches the whole local network. Use the broadcast address of a subnet
    /// (e.g. 192.168.1.255) when the computer running wol has several networks.
    #[arg(long, value_name = "IPV4")]
    broadcast: Option<Ipv4Addr>,

    /// UDP port of the magic packets [default: 9]
    ///
    /// Network cards accept any port; some routers or firewalls only let port 7 or 9 through.
    #[arg(long)]
    port: Option<u16>,
}

/// Arguments of `wol edit`: only the given options change.
#[derive(Debug, Args)]
struct EditArgs {
    /// Name of the machine to change (case-insensitive)
    name: String,

    /// New machine name
    #[arg(long = "name", value_name = "NAME")]
    new_name: Option<String>,

    /// Replace all network interfaces (repeatable)
    ///
    /// Same format as `wol add --mac`. Cannot be combined with --add-mac or --remove-mac.
    #[arg(long = "mac", value_name = "MAC[=LABEL]", value_parser = parse_interface, conflicts_with_all = ["add_interfaces", "remove_interfaces"])]
    interfaces: Vec<Interface>,

    /// Add a network interface (repeatable)
    ///
    /// When the machine already has this MAC address, its label is replaced instead
    /// (removed when no =LABEL is given).
    #[arg(long = "add-mac", value_name = "MAC[=LABEL]", value_parser = parse_interface)]
    add_interfaces: Vec<Interface>,

    /// Remove the network interface with this MAC address (repeatable)
    ///
    /// A machine keeps at least one interface.
    #[arg(long = "remove-mac", value_name = "MAC")]
    remove_interfaces: Vec<MacAddress>,

    /// Move the machine to this group
    #[arg(long, conflicts_with = "no_group")]
    group: Option<String>,

    /// Remove the machine from its group
    #[arg(long)]
    no_group: bool,

    /// New destination address of the magic packets
    #[arg(long, value_name = "IPV4", conflicts_with = "no_broadcast")]
    broadcast: Option<Ipv4Addr>,

    /// Go back to the default destination address (255.255.255.255)
    #[arg(long)]
    no_broadcast: bool,

    /// New UDP port of the magic packets
    #[arg(long, conflicts_with = "no_port")]
    port: Option<u16>,

    /// Go back to the default UDP port (9)
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
        /// Unique group name (case-insensitive, up to 64 characters)
        name: String,
    },

    /// Rename a group (its machines follow)
    Rename {
        /// Current name (case-insensitive)
        name: String,

        /// New name
        new_name: String,
    },

    /// Remove a group
    Remove {
        /// Name of the group to remove (case-insensitive)
        name: String,

        /// Keep the group's machines, without a group
        ///
        /// Required when the group still has machines; machines are never removed with
        /// their group.
        #[arg(long)]
        ungroup: bool,
    },

    /// Move a group to another position in the list
    Move {
        /// Name of the group to move (case-insensitive)
        name: String,

        /// New position, starting at 1 (beyond the end: last)
        position: usize,
    },
}

/// Result of a successful command: what to print in each output mode, and the exit code.
pub struct Outcome {
    /// JSON document (without the `schema` field, added when printing).
    pub(crate) json: Value,

    /// Human-readable text.
    pub(crate) text: String,

    /// Process exit code (non-zero for partial failures).
    pub(crate) exit: u8,

    /// Whether the command already printed its output as it went (`wake --wait`): only the
    /// configuration line is left to print.
    pub(crate) streamed: bool,
}

impl Outcome {
    /// Builds a fully successful outcome.
    fn ok(json: Value, text: String) -> Self {
        Self {
            json,
            text,
            exit: 0,
            streamed: false,
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

    let Some(command) = &cli.command else {
        print!("{}", overview(cli.config.as_deref()));
        return ExitCode::SUCCESS;
    };

    // Needs no configuration: it must work even when the file is invalid.
    if let Command::Version = command {
        let version = env!("CARGO_PKG_VERSION");
        if cli.json {
            println!("{}", document(None, json!({ "version": version })));
        } else {
            println!("wol {version} (JSON schema {SCHEMA})");
        }
        return ExitCode::SUCCESS;
    }

    let mut file = match ConfigFile::load(config::locate(cli.config.as_deref())) {
        Ok(file) => file,
        Err(error) => {
            // The error itself names the file (invalid or unreadable).
            report_error(cli.json, &error, None);
            return ExitCode::from(error.exit_code());
        }
    };

    let result = execute(command, cli.json, &mut file);
    let config = json!({ "path": file.path(), "status": file.status() });

    match result {
        Ok(outcome) => {
            if cli.json {
                if !outcome.streamed {
                    println!("{}", document(Some(config), outcome.json));
                }
            } else {
                print!("{}", outcome.text);
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

/// Prints one NDJSON line.
pub(crate) fn emit(event: &Value) {
    emit_text(&event.to_string());
}

/// Prints a line and flushes it; exits quietly when the consumer has gone (closed pipe).
pub(crate) fn emit_text(line: &str) {
    let mut stdout = io::stdout().lock();
    if writeln!(stdout, "{line}")
        .and_then(|()| stdout.flush())
        .is_err()
    {
        std::process::exit(0);
    }
}

/// Builds the text printed by `wol` without a command: the help summary, with the
/// configuration file in use between the usage line and the command list.
fn overview(explicit: Option<&Path>) -> String {
    let path = config::locate(explicit);
    let state = if path.is_file() {
        ""
    } else {
        " (not created yet)"
    };
    let line = format!(
        "Configuration: {}{state}

",
        path.display()
    );

    let help = Cli::command().render_help().to_string();
    match help.find("Commands:") {
        Some(index) => format!("{}{line}{}", &help[..index], &help[index..]),
        None => format!(
            "{help}
{line}"
        ),
    }
}

/// Reports an error on `stderr`; in JSON, with the configuration file when it was loaded.
fn report_error(json: bool, error: &Error, file: Option<&ConfigFile>) {
    if json {
        let config = file.map(|file| json!({ "path": file.path(), "status": file.status() }));
        let body = json!({ "error": { "code": error.code(), "message": error.to_string(), "details": error.details() } });
        eprintln!("{}", document(config, body));
    } else {
        eprintln!("error: {error}");
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
fn execute(command: &Command, json: bool, file: &mut ConfigFile) -> Result<Outcome> {
    match command {
        Command::List => Ok(list(file)),
        Command::Wake {
            machines,
            groups,
            wait,
        } => wake::wake(
            file,
            &wake::Options {
                names: machines,
                groups,
                wait: wait.map(std::time::Duration::from_secs),
                json,
            },
        ),
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
                json,
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
        Command::Version => unreachable!("handled before loading the configuration"),
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
        streamed: false,
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
