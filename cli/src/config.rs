//! The `wol.toml` configuration file: lookup, validation and comment-preserving edits.
//!
//! The file is read through `toml` (typed model) and edited through `toml_edit`, so that
//! comments and layout written by hand survive every change made by the CLI.

use std::fs;
use std::io;
use std::net::Ipv4Addr;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use toml_edit::{Array, ArrayOfTables, DocumentMut, Item, Table, value};

use crate::error::{Error, Result};
use crate::mac::MacAddress;

/// Name of the configuration file.
pub const FILE_NAME: &str = "wol.toml";

/// Name of the application directory inside the user configuration directory.
pub const APP_DIR: &str = "wol";

/// Maximum length of a machine or group name, in characters.
pub const MAX_NAME_LEN: usize = 64;

/// Maximum length of a host name, in characters (DNS limit).
pub const MAX_HOST_LEN: usize = 253;

/// Languages the CLI can be configured with.
pub const LANGUAGES: [&str; 2] = ["en", "fr"];

/// Typed content of the configuration file.
#[derive(Debug, Default, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Config {
    /// CLI settings.
    #[serde(default)]
    pub settings: Settings,

    /// Groups, in display order.
    #[serde(default, rename = "group")]
    pub groups: Vec<Group>,

    /// Machines, in file order.
    #[serde(default, rename = "machine")]
    pub machines: Vec<Machine>,
}

/// CLI settings (`[settings]` table).
#[derive(Debug, Default, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Settings {
    /// Language of human-readable output (`en`, `fr`); system language when absent.
    pub language: Option<String>,
}

/// A group of machines (`[[group]]` table).
#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Group {
    /// Unique group name.
    pub name: String,
}

/// A machine (`[[machine]]` table).
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Machine {
    /// Unique machine name.
    pub name: String,

    /// Name of the group the machine belongs to, if any.
    #[serde(default)]
    pub group: Option<String>,

    /// MAC address the magic packet targets.
    pub mac: MacAddress,

    /// IP address or host name used for status checks.
    pub host: String,

    /// Destination address of the magic packet; limited broadcast when absent.
    #[serde(default)]
    pub broadcast: Option<Ipv4Addr>,

    /// UDP port of the magic packet; port 9 when absent.
    #[serde(default)]
    pub port: Option<u16>,

    /// TCP ports tried when ICMP is unavailable.
    #[serde(default)]
    pub check_ports: Vec<u16>,
}

/// Tells whether two machine or group names designate the same entity (case-insensitive).
pub fn same_name(a: &str, b: &str) -> bool {
    a.to_lowercase() == b.to_lowercase()
}

/// Checks a machine or group name.
///
/// # Errors
///
/// Returns the reason when the name is empty, padded with spaces or too long.
fn check_name(name: &str) -> std::result::Result<(), String> {
    if name.trim().is_empty() {
        return Err("name must not be empty".to_owned());
    }
    if name.trim() != name {
        return Err("name must not start or end with spaces".to_owned());
    }
    if name.chars().count() > MAX_NAME_LEN {
        return Err(format!("name must not exceed {MAX_NAME_LEN} characters"));
    }
    Ok(())
}

/// Checks the fields of a machine that do not depend on the rest of the configuration.
///
/// # Errors
///
/// Returns the offending field and the reason.
fn check_machine(machine: &Machine) -> std::result::Result<(), (&'static str, String)> {
    check_name(&machine.name).map_err(|reason| ("name", reason))?;

    let host = machine.host.trim();
    if host.is_empty()
        || host != machine.host
        || host.chars().count() > MAX_HOST_LEN
        || host.contains(char::is_whitespace)
    {
        return Err((
            "host",
            format!("expected an IP address or a host name of at most {MAX_HOST_LEN} characters"),
        ));
    }
    if machine.port == Some(0) {
        return Err(("port", "port must be between 1 and 65535".to_owned()));
    }
    if machine.check_ports.contains(&0) {
        return Err((
            "check_ports",
            "ports must be between 1 and 65535".to_owned(),
        ));
    }
    Ok(())
}

impl Config {
    /// Checks the rules the TOML grammar cannot express: names, uniqueness, group references.
    ///
    /// # Errors
    ///
    /// Returns a description of the first problem found.
    fn validate(&self) -> std::result::Result<(), String> {
        if let Some(language) = &self.settings.language {
            if !LANGUAGES.contains(&language.as_str()) {
                return Err(format!(
                    "unsupported language \"{language}\" (expected one of: {})",
                    LANGUAGES.join(", ")
                ));
            }
        }

        for (index, group) in self.groups.iter().enumerate() {
            check_name(&group.name)
                .map_err(|reason| format!("group \"{}\": {reason}", group.name))?;
            if self.groups[..index]
                .iter()
                .any(|other| same_name(&other.name, &group.name))
            {
                return Err(format!("group \"{}\" is defined twice", group.name));
            }
        }

        for (index, machine) in self.machines.iter().enumerate() {
            check_machine(machine).map_err(|(field, reason)| {
                format!("machine \"{}\", {field}: {reason}", machine.name)
            })?;
            if self.machines[..index]
                .iter()
                .any(|other| same_name(&other.name, &machine.name))
            {
                return Err(format!("machine \"{}\" is defined twice", machine.name));
            }
            if let Some(group) = &machine.group {
                if self.group(group).is_none() {
                    return Err(format!(
                        "machine \"{}\" refers to undefined group \"{group}\"",
                        machine.name
                    ));
                }
            }
        }

        Ok(())
    }

    /// Finds a group by name (case-insensitive).
    pub fn group(&self, name: &str) -> Option<&Group> {
        self.groups
            .iter()
            .find(|group| same_name(&group.name, name))
    }

    /// Finds the index of a machine by name (case-insensitive).
    pub fn machine_index(&self, name: &str) -> Option<usize> {
        self.machines
            .iter()
            .position(|machine| same_name(&machine.name, name))
    }

    /// Finds the index of a group by name (case-insensitive).
    pub fn group_index(&self, name: &str) -> Option<usize> {
        self.groups
            .iter()
            .position(|group| same_name(&group.name, name))
    }

    /// Lists the machines of a group, in file order.
    pub fn group_machines<'a>(&'a self, group: &'a str) -> impl Iterator<Item = &'a Machine> + 'a {
        self.machines.iter().filter(move |machine| {
            machine
                .group
                .as_deref()
                .is_some_and(|name| same_name(name, group))
        })
    }
}

/// Resolves the configuration file path.
///
/// Lookup order: explicit path, `wol.toml` next to the executable (portable mode),
/// then the user configuration directory.
pub fn locate(explicit: Option<&Path>) -> PathBuf {
    if let Some(path) = explicit {
        return path.to_owned();
    }

    let portable = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|dir| dir.join(FILE_NAME)))
        .filter(|path| path.is_file());

    portable
        .or_else(|| dirs::config_dir().map(|dir| dir.join(APP_DIR).join(FILE_NAME)))
        .unwrap_or_else(|| PathBuf::from(FILE_NAME))
}

/// Computes the 1-based line of a byte offset.
///
/// Counting `\n` bytes is safe on UTF-8: bytes of multi-byte sequences are always >= 0x80.
fn line_of(text: &str, offset: usize) -> usize {
    text.as_bytes()[..offset.min(text.len())]
        .iter()
        .filter(|&&byte| byte == b'\n')
        .count()
        + 1
}

/// Writes or removes an optional key of a TOML table, keeping its position when it exists.
fn set_optional(table: &mut Table, key: &str, item: Option<Item>) {
    match item {
        Some(item) => {
            table[key] = item;
        }
        None => {
            table.remove(key);
        }
    }
}

/// Writes the fields of `machine` into a `[[machine]]` table, keeping comments and key order.
fn write_machine(table: &mut Table, machine: &Machine) {
    table["name"] = value(&machine.name);
    set_optional(table, "group", machine.group.as_deref().map(value));
    table["mac"] = value(machine.mac.to_string());
    table["host"] = value(&machine.host);
    set_optional(
        table,
        "broadcast",
        machine.broadcast.map(|address| value(address.to_string())),
    );
    set_optional(
        table,
        "port",
        machine.port.map(|port| value(i64::from(port))),
    );
    set_optional(
        table,
        "check_ports",
        (!machine.check_ports.is_empty()).then(|| {
            value(
                machine
                    .check_ports
                    .iter()
                    .map(|&port| i64::from(port))
                    .collect::<Array>(),
            )
        }),
    );
}

/// The configuration file loaded in memory, with its typed model and its editable document.
#[derive(Debug)]
pub struct ConfigFile {
    /// Path of the file (it may not exist yet).
    path: PathBuf,

    /// Editable TOML document, preserving comments and layout.
    document: DocumentMut,

    /// Typed and validated content, kept in sync with `document`.
    config: Config,
}

impl ConfigFile {
    /// Loads the configuration file; a missing file is an empty configuration.
    ///
    /// # Errors
    ///
    /// Returns [`Error::Io`] when the file cannot be read and [`Error::ConfigInvalid`]
    /// when its content is invalid.
    pub fn load(path: PathBuf) -> Result<Self> {
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == io::ErrorKind::NotFound => String::new(),
            Err(source) => return Err(Error::Io { path, source }),
        };

        let config = Self::parse(&path, &text)?;
        let document = text
            .parse::<DocumentMut>()
            .map_err(|error| Error::ConfigInvalid {
                line: error.span().map(|span| line_of(&text, span.start)),
                message: error.message().to_owned(),
                path: path.clone(),
            })?;

        Ok(Self {
            path,
            document,
            config,
        })
    }

    /// Parses and validates the typed model.
    ///
    /// # Errors
    ///
    /// Returns [`Error::ConfigInvalid`] with the line of the error when known.
    fn parse(path: &Path, text: &str) -> Result<Config> {
        let config: Config = toml::from_str(text).map_err(|error| Error::ConfigInvalid {
            path: path.to_owned(),
            line: error.span().map(|span| line_of(text, span.start)),
            message: error.message().to_owned(),
        })?;

        config.validate().map_err(|message| Error::ConfigInvalid {
            path: path.to_owned(),
            line: None,
            message,
        })?;

        Ok(config)
    }

    /// Path of the configuration file.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Typed content of the configuration.
    pub fn config(&self) -> &Config {
        &self.config
    }

    /// Writes the document to disk atomically (temporary file, then rename).
    ///
    /// # Errors
    ///
    /// Returns [`Error::Io`] when the directory or the file cannot be written.
    pub fn save(&self) -> Result<()> {
        let io_error = |path: &Path| {
            let path = path.to_owned();
            move |source| Error::Io { path, source }
        };

        if let Some(dir) = self.path.parent().filter(|dir| !dir.as_os_str().is_empty()) {
            fs::create_dir_all(dir).map_err(io_error(dir))?;
        }

        let temporary = self.path.with_extension("toml.tmp");
        fs::write(&temporary, self.document.to_string()).map_err(io_error(&temporary))?;
        fs::rename(&temporary, &self.path).map_err(io_error(&self.path))
    }

    /// Re-reads the typed model from the edited document, guaranteeing it is still valid.
    ///
    /// # Errors
    ///
    /// Returns [`Error::ConfigInvalid`] if an edit produced an invalid document (a bug).
    fn refresh(&mut self) -> Result<()> {
        self.config = Self::parse(&self.path, &self.document.to_string())?;
        Ok(())
    }

    /// Returns the array of tables stored under `key`, creating it when missing.
    ///
    /// # Errors
    ///
    /// Returns [`Error::ConfigInvalid`] when `key` holds something else (e.g. an inline array).
    fn tables_mut(&mut self, key: &str) -> Result<&mut ArrayOfTables> {
        let path = self.path.clone();
        self.document
            .as_table_mut()
            .entry(key)
            .or_insert(Item::ArrayOfTables(ArrayOfTables::new()))
            .as_array_of_tables_mut()
            .ok_or_else(|| Error::ConfigInvalid {
                path,
                line: None,
                message: format!(
                    "\"{key}\" must be written as [[{key}]] tables to be edited by wol"
                ),
            })
    }

    /// Appends a new table under `key`, separated from the previous content by a blank line.
    ///
    /// # Errors
    ///
    /// See [`ConfigFile::tables_mut`].
    fn push_table(&mut self, key: &str, mut table: Table) -> Result<()> {
        let empty = self.document.to_string().trim().is_empty();
        table.decor_mut().set_prefix(if empty { "" } else { "\n" });
        self.tables_mut(key)?.push(table);
        Ok(())
    }

    /// Checks a machine against the configuration, `replacing` the machine at that index if any.
    ///
    /// Returns the machine with its group name normalized to the group's actual spelling.
    ///
    /// # Errors
    ///
    /// Returns [`Error::InvalidValue`], [`Error::MachineAlreadyExists`] or [`Error::GroupNotFound`].
    fn checked_machine(&self, machine: &Machine, replacing: Option<usize>) -> Result<Machine> {
        check_machine(machine).map_err(|(field, reason)| Error::InvalidValue { field, reason })?;

        let duplicate = self
            .config
            .machines
            .iter()
            .enumerate()
            .any(|(index, other)| {
                Some(index) != replacing && same_name(&other.name, &machine.name)
            });
        if duplicate {
            return Err(Error::MachineAlreadyExists(machine.name.clone()));
        }

        let mut machine = machine.clone();
        if let Some(group) = &machine.group {
            let group = self
                .config
                .group(group)
                .ok_or_else(|| Error::GroupNotFound(group.clone()))?;
            machine.group = Some(group.name.clone());
        }

        Ok(machine)
    }

    /// Adds a machine at the end of the file.
    ///
    /// # Errors
    ///
    /// See [`ConfigFile::checked_machine`].
    pub fn add_machine(&mut self, machine: &Machine) -> Result<Machine> {
        let machine = self.checked_machine(machine, None)?;

        let mut table = Table::new();
        write_machine(&mut table, &machine);
        self.push_table("machine", table)?;
        self.refresh()?;

        Ok(machine)
    }

    /// Replaces the machine named `name` with `machine`, keeping its comments and position.
    ///
    /// # Errors
    ///
    /// Returns [`Error::MachineNotFound`], or see [`ConfigFile::checked_machine`].
    pub fn edit_machine(&mut self, name: &str, machine: &Machine) -> Result<Machine> {
        let index = self
            .config
            .machine_index(name)
            .ok_or_else(|| Error::MachineNotFound(name.to_owned()))?;
        let machine = self.checked_machine(machine, Some(index))?;

        let table = self
            .tables_mut("machine")?
            .get_mut(index)
            .expect("model and document are in sync");
        write_machine(table, &machine);
        self.refresh()?;

        Ok(machine)
    }

    /// Removes the machine named `name` and returns it.
    ///
    /// # Errors
    ///
    /// Returns [`Error::MachineNotFound`].
    pub fn remove_machine(&mut self, name: &str) -> Result<Machine> {
        let index = self
            .config
            .machine_index(name)
            .ok_or_else(|| Error::MachineNotFound(name.to_owned()))?;
        let machine = self.config.machines[index].clone();

        self.tables_mut("machine")?.remove(index);
        self.refresh()?;

        Ok(machine)
    }

    /// Adds a group at the end of the group list.
    ///
    /// # Errors
    ///
    /// Returns [`Error::InvalidValue`] or [`Error::GroupAlreadyExists`].
    pub fn add_group(&mut self, name: &str) -> Result<Group> {
        check_name(name).map_err(|reason| Error::InvalidValue {
            field: "name",
            reason,
        })?;
        if self.config.group(name).is_some() {
            return Err(Error::GroupAlreadyExists(name.to_owned()));
        }

        let mut table = Table::new();
        table["name"] = value(name);
        self.push_table("group", table)?;
        self.refresh()?;

        Ok(Group {
            name: name.to_owned(),
        })
    }

    /// Renames a group and updates the machines that belong to it.
    ///
    /// # Errors
    ///
    /// Returns [`Error::GroupNotFound`], [`Error::InvalidValue`] or [`Error::GroupAlreadyExists`].
    pub fn rename_group(&mut self, name: &str, new_name: &str) -> Result<Group> {
        let index = self
            .config
            .group_index(name)
            .ok_or_else(|| Error::GroupNotFound(name.to_owned()))?;
        check_name(new_name).map_err(|reason| Error::InvalidValue {
            field: "name",
            reason,
        })?;
        let taken = self
            .config
            .groups
            .iter()
            .enumerate()
            .any(|(other, group)| other != index && same_name(&group.name, new_name));
        if taken {
            return Err(Error::GroupAlreadyExists(new_name.to_owned()));
        }

        let members: Vec<usize> = self
            .config
            .machines
            .iter()
            .enumerate()
            .filter(|(_, machine)| {
                machine
                    .group
                    .as_deref()
                    .is_some_and(|group| same_name(group, name))
            })
            .map(|(index, _)| index)
            .collect();

        self.tables_mut("group")?
            .get_mut(index)
            .expect("model and document are in sync")["name"] = value(new_name);
        let machines = self.tables_mut("machine")?;
        for member in members {
            machines
                .get_mut(member)
                .expect("model and document are in sync")["group"] = value(new_name);
        }
        self.refresh()?;

        Ok(Group {
            name: new_name.to_owned(),
        })
    }

    /// Removes a group; its machines are kept without a group when `ungroup` is set.
    ///
    /// Returns the removed group and the names of the machines that lost their group.
    ///
    /// # Errors
    ///
    /// Returns [`Error::GroupNotFound`], or [`Error::GroupNotEmpty`] when the group still
    /// has machines and `ungroup` is not set.
    pub fn remove_group(&mut self, name: &str, ungroup: bool) -> Result<(Group, Vec<String>)> {
        let index = self
            .config
            .group_index(name)
            .ok_or_else(|| Error::GroupNotFound(name.to_owned()))?;
        let group = self.config.groups[index].clone();

        let members: Vec<(usize, String)> = self
            .config
            .machines
            .iter()
            .enumerate()
            .filter(|(_, machine)| {
                machine
                    .group
                    .as_deref()
                    .is_some_and(|other| same_name(other, name))
            })
            .map(|(index, machine)| (index, machine.name.clone()))
            .collect();
        if !members.is_empty() && !ungroup {
            return Err(Error::GroupNotEmpty {
                name: group.name,
                machines: members.into_iter().map(|(_, name)| name).collect(),
            });
        }

        let machines = self.tables_mut("machine")?;
        for (member, _) in &members {
            machines
                .get_mut(*member)
                .expect("model and document are in sync")
                .remove("group");
        }
        self.tables_mut("group")?.remove(index);
        self.refresh()?;

        Ok((group, members.into_iter().map(|(_, name)| name).collect()))
    }

    /// Moves a group to a 1-based `position` in the group list (clamped to the list bounds).
    ///
    /// Returns the group and its actual 1-based position.
    ///
    /// # Errors
    ///
    /// Returns [`Error::GroupNotFound`] or [`Error::InvalidValue`] when `position` is 0.
    pub fn move_group(&mut self, name: &str, position: usize) -> Result<(Group, usize)> {
        let index = self
            .config
            .group_index(name)
            .ok_or_else(|| Error::GroupNotFound(name.to_owned()))?;
        if position == 0 {
            return Err(Error::InvalidValue {
                field: "position",
                reason: "positions start at 1".to_owned(),
            });
        }
        let target = (position - 1).min(self.config.groups.len() - 1);

        // toml_edit renders tables by document position: reorder the tables, then hand the
        // original positions back in order so the [[group]] blocks actually swap places.
        let groups = self.tables_mut("group")?;
        let mut tables: Vec<Table> = (0..groups.len()).map(|_| groups.remove(0)).collect();
        let positions: Vec<Option<isize>> = tables.iter().map(Table::position).collect();
        let moved = tables.remove(index);
        tables.insert(target, moved);
        for (mut table, position) in tables.into_iter().zip(positions) {
            table.set_position(position);
            groups.push(table);
        }
        self.refresh()?;

        Ok((self.config.groups[target].clone(), target + 1))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"# Hand-written comment
[[group]]
name = "Office"

[[group]]
name = "Servers"

# The main PC
[[machine]]
name = "Office PC"
group = "Office"
mac = "a4-bb-6d-12-34-56"
host = "192.168.1.20"
"#;

    /// Loads `text` as if it were a configuration file.
    fn load(text: &str) -> Result<ConfigFile> {
        let path = PathBuf::from("wol.toml");
        let config = ConfigFile::parse(&path, text)?;
        Ok(ConfigFile {
            path,
            document: text.parse().unwrap(),
            config,
        })
    }

    /// Builds a machine with only its required fields.
    fn machine(name: &str) -> Machine {
        Machine {
            name: name.to_owned(),
            group: None,
            mac: "00:11:22:33:44:55".parse().unwrap(),
            host: "nas.local".to_owned(),
            broadcast: None,
            port: None,
            check_ports: Vec::new(),
        }
    }

    #[test]
    fn parses_sample() {
        let file = load(SAMPLE).unwrap();
        let config = file.config();
        assert_eq!(config.groups.len(), 2);
        assert_eq!(config.machines[0].mac.to_string(), "A4:BB:6D:12:34:56");
        assert_eq!(config.group_machines("office").count(), 1);
    }

    #[test]
    fn reports_syntax_error_line() {
        let error = load("[[machine]]\nname = \"PC\"\nmac = \n").unwrap_err();
        assert!(
            matches!(error, Error::ConfigInvalid { line: Some(3), .. }),
            "{error:?}"
        );
    }

    #[test]
    fn rejects_invalid_content() {
        for text in [
            "[[machine]]\nname = \"PC\"\nmac = \"nope\"\nhost = \"pc\"\n",
            "[[machine]]\nname = \"PC\"\nmac = \"00:11:22:33:44:55\"\nhost = \"pc\"\ngroup = \"Missing\"\n",
            "[[group]]\nname = \"A\"\n[[group]]\nname = \"a\"\n",
            "[[machine]]\nname = \"PC\"\nmac = \"00:11:22:33:44:55\"\nhost = \"pc\"\ncolour = \"red\"\n",
            "[settings]\nlanguage = \"de\"\n",
        ] {
            assert!(
                matches!(load(text), Err(Error::ConfigInvalid { .. })),
                "{text}"
            );
        }
    }

    #[test]
    fn edits_preserve_comments() {
        let mut file = load(SAMPLE).unwrap();
        file.add_machine(&Machine {
            group: Some("servers".to_owned()),
            ..machine("NAS")
        })
        .unwrap();

        let text = file.document.to_string();
        assert!(text.contains("# Hand-written comment"));
        assert!(text.contains("# The main PC"));
        assert_eq!(file.config().machines[1].group.as_deref(), Some("Servers"));
    }

    #[test]
    fn rejects_duplicates_and_unknown_groups() {
        let mut file = load(SAMPLE).unwrap();
        assert!(matches!(
            file.add_machine(&machine("office pc")),
            Err(Error::MachineAlreadyExists(_))
        ));
        assert!(matches!(
            file.add_machine(&Machine {
                group: Some("Home".to_owned()),
                ..machine("NAS")
            }),
            Err(Error::GroupNotFound(_))
        ));
        assert!(matches!(
            file.add_group("OFFICE"),
            Err(Error::GroupAlreadyExists(_))
        ));
    }

    #[test]
    fn edit_keeps_position_and_can_rename() {
        let mut file = load(SAMPLE).unwrap();
        file.add_machine(&machine("NAS")).unwrap();
        file.edit_machine(
            "office pc",
            &Machine {
                name: "Desk".to_owned(),
                ..machine("unused")
            },
        )
        .unwrap();

        let names: Vec<&str> = file
            .config()
            .machines
            .iter()
            .map(|machine| machine.name.as_str())
            .collect();
        assert_eq!(names, ["Desk", "NAS"]);
        assert!(file.document.to_string().contains("# The main PC"));
    }

    #[test]
    fn rename_group_updates_members() {
        let mut file = load(SAMPLE).unwrap();
        file.rename_group("office", "Work").unwrap();
        assert_eq!(file.config().machines[0].group.as_deref(), Some("Work"));
    }

    #[test]
    fn remove_group_requires_ungroup_when_not_empty() {
        let mut file = load(SAMPLE).unwrap();
        assert!(matches!(
            file.remove_group("Office", false),
            Err(Error::GroupNotEmpty { .. })
        ));

        let (_, ungrouped) = file.remove_group("Office", true).unwrap();
        assert_eq!(ungrouped, ["Office PC"]);
        assert_eq!(file.config().machines[0].group, None);
        assert_eq!(file.config().groups.len(), 1);
    }

    #[test]
    fn move_group_reorders_document() {
        let mut file = load(SAMPLE).unwrap();
        let (_, position) = file.move_group("Servers", 1).unwrap();
        assert_eq!(position, 1);

        let names: Vec<&str> = file
            .config()
            .groups
            .iter()
            .map(|group| group.name.as_str())
            .collect();
        assert_eq!(names, ["Servers", "Office"]);

        let text = file.document.to_string();
        assert!(
            text.find("Servers").unwrap() < text.find("\"Office\"").unwrap(),
            "{text}"
        );
    }
}
