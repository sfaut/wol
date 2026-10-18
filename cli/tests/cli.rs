//! End-to-end tests of the `wol` binary: output, JSON contract and exit codes.

use std::net::{Ipv4Addr, SocketAddr, UdpSocket};
use std::path::PathBuf;

use assert_cmd::Command;
use serde_json::Value;
use tempfile::TempDir;

/// A `wol` invocation bound to a configuration file in a temporary directory.
struct Wol {
    /// Temporary directory holding the configuration file.
    dir: TempDir,

    /// Devices the fake network scan finds (`WOL_FAKE_SCAN` syntax).
    network: String,
}

impl Wol {
    /// Creates an empty test environment.
    fn new() -> Self {
        Self {
            dir: TempDir::new().unwrap(),
            network: String::new(),
        }
    }

    /// Sets the devices the fake network scan finds (`IP=MAC` pairs, or `none`).
    fn network(mut self, devices: &str) -> Self {
        devices.clone_into(&mut self.network);
        self
    }

    /// Path of the configuration file.
    fn config(&self) -> PathBuf {
        self.dir.path().join("wol.toml")
    }

    /// Builds a `wol` command using the test configuration file.
    fn command(&self, args: &[&str]) -> Command {
        let mut command = Command::cargo_bin("wol").unwrap();
        command.arg("--config").arg(self.config()).args(args);
        command.env("WOL_FAKE_SCAN", &self.network);
        command
    }

    /// Runs a command that must succeed.
    fn ok(&self, args: &[&str]) {
        self.command(args).assert().success();
    }

    /// Runs a command with `--json` that prints NDJSON; returns its exit code and events.
    fn ndjson(&self, args: &[&str]) -> (i32, Vec<Value>) {
        let output = self.command(args).arg("--json").output().unwrap();
        let events = String::from_utf8(output.stdout)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        (output.status.code().unwrap(), events)
    }

    /// Runs a command with `--json` and returns its exit code, `stdout` and `stderr` documents.
    fn json(&self, args: &[&str]) -> (i32, Option<Value>, Option<Value>) {
        let output = self.command(args).arg("--json").output().unwrap();
        let parse = |bytes: &[u8]| {
            (!bytes.is_empty()).then(|| serde_json::from_slice::<Value>(bytes).unwrap())
        };
        (
            output.status.code().unwrap(),
            parse(&output.stdout),
            parse(&output.stderr),
        )
    }
}

#[test]
fn list_on_missing_file_is_empty() {
    let wol = Wol::new();
    let (code, stdout, _) = wol.json(&["list"]);

    assert_eq!(code, 0);
    let stdout = stdout.unwrap();
    assert_eq!(stdout["schema"], 1);
    assert_eq!(stdout["machines"], Value::Array(Vec::new()));
    assert!(!wol.config().exists(), "listing must not create the file");
}

#[test]
fn add_then_list_round_trips() {
    let wol = Wol::new();
    wol.ok(&["group", "add", "Office"]);
    wol.ok(&[
        "add",
        "Office PC",
        "--mac",
        "a4-bb-6d-12-34-56=Ethernet",
        "--mac",
        "a4bb6d123457",
        "--group",
        "office",
    ]);

    let (_, stdout, _) = wol.json(&["list"]);
    let machine = &stdout.unwrap()["machines"][0];
    assert_eq!(machine["name"], "Office PC");
    assert_eq!(machine["group"], "Office");
    assert_eq!(
        machine["interfaces"],
        serde_json::json!([
            { "mac": "A4:BB:6D:12:34:56", "label": "Ethernet" },
            { "mac": "A4:BB:6D:12:34:57", "label": null },
        ])
    );
    assert_eq!(machine["broadcast"], Value::Null);

    let text = std::fs::read_to_string(wol.config()).unwrap();
    assert!(text.starts_with("[[group]]"), "{text}");
}

#[test]
fn reports_config_file_and_status() {
    let wol = Wol::new();
    let path = wol.config().display().to_string();

    let status = |args: &[&str]| {
        let (_, stdout, _) = wol.json(args);
        let config = stdout.unwrap()["config"].clone();
        assert_eq!(config["path"], path.as_str(), "{args:?}");
        config["status"].as_str().unwrap().to_owned()
    };
    assert_eq!(status(&["list"]), "missing");
    assert_eq!(status(&["group", "add", "Office"]), "created");
    assert_eq!(status(&["group", "add", "Home"]), "updated");
    assert_eq!(status(&["list"]), "loaded");

    // Human output names the file on stderr, keeping stdout for data.
    let output = wol.command(&["list"]).output().unwrap();
    let stderr = String::from_utf8(output.stderr).unwrap();
    assert_eq!(stderr.trim_end(), format!("Configuration: {path}"));

    // Errors after loading also name the file.
    let (_, _, stderr) = wol.json(&["wake", "Ghost"]);
    assert_eq!(stderr.unwrap()["config"]["status"], "loaded");
}

#[test]
fn schema_comes_first() {
    let wol = Wol::new();
    let output = wol.command(&["list", "--json"]).output().unwrap();
    assert!(
        String::from_utf8(output.stdout)
            .unwrap()
            .starts_with("{\"schema\":1,")
    );
}

#[test]
fn errors_have_codes_and_exit_codes() {
    let wol = Wol::new();
    wol.ok(&["add", "NAS", "--mac", "001122334455"]);

    let cases: [(&[&str], i32, &str); 5] = [
        (&["wake", "Ghost"], 4, "machine_not_found"),
        (&["wake", "--group", "Ghost"], 4, "group_not_found"),
        (
            &["add", "nas", "--mac", "001122334455"],
            2,
            "machine_already_exists",
        ),
        (&["add", "PC", "--mac", "nope"], 2, "invalid_usage"),
        (&["edit", "NAS", "--port", "0"], 2, "invalid_value"),
    ];
    for (args, expected_code, expected_error) in cases {
        let (code, stdout, stderr) = wol.json(args);
        assert_eq!(code, expected_code, "{args:?}");
        assert!(stdout.is_none(), "{args:?}");
        assert_eq!(stderr.unwrap()["error"]["code"], expected_error, "{args:?}");
    }
}

#[test]
fn invalid_file_reports_line() {
    let wol = Wol::new();
    std::fs::write(wol.config(), "[[machine]]\nname = \"PC\"\nmac = \n").unwrap();

    let (code, _, stderr) = wol.json(&["list"]);
    assert_eq!(code, 3);
    let error = &stderr.unwrap()["error"];
    assert_eq!(error["code"], "config_invalid");
    assert_eq!(error["details"]["line"], 3);
}

#[test]
fn wake_sends_magic_packets_once_per_machine() {
    let receiver = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
    let SocketAddr::V4(address) = receiver.local_addr().unwrap() else {
        unreachable!("bound to an IPv4 address");
    };
    let port = address.port().to_string();

    let wol = Wol::new();
    wol.ok(&["group", "add", "Servers"]);
    wol.ok(&[
        "add",
        "NAS",
        "--mac",
        "00:11:22:33:44:55",
        "--mac",
        "00:11:22:33:44:56",
        "--group",
        "Servers",
        "--broadcast",
        "127.0.0.1",
        "--port",
        &port,
    ]);

    // Named twice (directly and through its group): woken once.
    let (code, stdout, _) = wol.json(&["wake", "nas", "--group", "servers"]);
    assert_eq!(code, 0);
    let results = stdout.unwrap()["results"].as_array().unwrap().clone();
    assert_eq!(results.len(), 1);
    assert_eq!(results[0]["status"], "sent");
    assert_eq!(results[0]["target"], format!("127.0.0.1:{port}"));

    assert_eq!(
        results[0]["macs"],
        serde_json::json!(["00:11:22:33:44:55", "00:11:22:33:44:56"])
    );

    // Every interface gets its packets (each one is sent several times).
    let mut targets = std::collections::BTreeSet::new();
    let mut buffer = [0u8; 256];
    receiver
        .set_read_timeout(Some(std::time::Duration::from_secs(2)))
        .unwrap();
    while let Ok((len, _)) = receiver.recv_from(&mut buffer) {
        assert_eq!(len, 102);
        targets.insert(buffer[6..12].to_vec());
        if targets.len() == 2 {
            break;
        }
    }
    assert_eq!(
        targets.into_iter().collect::<Vec<_>>(),
        [
            vec![0x00, 0x11, 0x22, 0x33, 0x44, 0x55],
            vec![0x00, 0x11, 0x22, 0x33, 0x44, 0x56]
        ]
    );
}

#[test]
fn edit_adds_relabels_and_removes_interfaces() {
    let wol = Wol::new();
    wol.ok(&["add", "PC", "--mac", "001122334455=Ethernet"]);
    wol.ok(&[
        "edit",
        "pc",
        "--add-mac",
        "001122334456=Wi-Fi",
        "--add-mac",
        "001122334455=LAN",
    ]);

    let (_, stdout, _) = wol.json(&["list"]);
    assert_eq!(
        stdout.unwrap()["machines"][0]["interfaces"],
        serde_json::json!([
            { "mac": "00:11:22:33:44:55", "label": "LAN" },
            { "mac": "00:11:22:33:44:56", "label": "Wi-Fi" },
        ])
    );

    wol.ok(&["edit", "PC", "--remove-mac", "00:11:22:33:44:55"]);
    let (code, _, stderr) = wol.json(&["edit", "PC", "--remove-mac", "00:11:22:33:44:56"]);
    assert_eq!(code, 2);
    assert_eq!(stderr.unwrap()["error"]["details"]["field"], "interfaces");

    let (code, _, _) = wol.json(&["edit", "PC", "--remove-mac", "00:11:22:33:44:99"]);
    assert_eq!(code, 2);
}

#[test]
fn group_lifecycle() {
    let wol = Wol::new();
    wol.ok(&["group", "add", "Office"]);
    wol.ok(&["group", "add", "Servers"]);
    wol.ok(&["add", "PC", "--mac", "001122334455", "--group", "Office"]);

    let (_, stdout, _) = wol.json(&["group", "move", "Servers", "1"]);
    assert_eq!(stdout.unwrap()["position"], 1);

    wol.ok(&["group", "rename", "Office", "Work"]);
    let (code, _, stderr) = wol.json(&["group", "remove", "Work"]);
    assert_eq!(code, 2);
    assert_eq!(
        stderr.unwrap()["error"]["details"]["machines"],
        serde_json::json!(["PC"])
    );

    let (_, stdout, _) = wol.json(&["group", "remove", "Work", "--ungroup"]);
    assert_eq!(stdout.unwrap()["ungrouped"], serde_json::json!(["PC"]));

    let (_, stdout, _) = wol.json(&["list"]);
    let stdout = stdout.unwrap();
    assert_eq!(stdout["groups"], serde_json::json!([{ "name": "Servers" }]));
    assert_eq!(stdout["machines"][0]["group"], Value::Null);
}

#[test]
fn status_reports_states() {
    let wol = Wol::new().network("192.168.1.20=00:11:22:33:44:55, 192.168.1.30=66:77:88:99:AA:BB");
    wol.ok(&["add", "Desk", "--mac", "001122334455"]);
    wol.ok(&["add", "NAS", "--mac", "001122334456"]);

    let (code, stdout, _) = wol.json(&["status"]);
    assert_eq!(code, 0);
    let stdout = stdout.unwrap();
    assert!(stdout["checked_at"].as_str().unwrap().ends_with('Z'));

    let machines = stdout["machines"].as_array().unwrap();
    assert_eq!(machines[0]["machine"], "Desk");
    assert_eq!(machines[0]["state"], "online");
    assert_eq!(machines[0]["mac"], "00:11:22:33:44:55");
    assert_eq!(machines[0]["address"], "192.168.1.20");
    assert_eq!(machines[1]["state"], "offline");
    assert_eq!(machines[1]["address"], Value::Null);

    let (_, stdout, _) = wol.json(&["status", "nas"]);
    assert_eq!(stdout.unwrap()["machines"].as_array().unwrap().len(), 1);
}

#[test]
fn status_without_local_network_is_an_error() {
    let wol = Wol::new().network("none");
    wol.ok(&["add", "Desk", "--mac", "001122334455"]);

    let (code, stdout, _) = wol.json(&["status"]);
    assert_eq!(code, 5);
    let machine = &stdout.unwrap()["machines"][0];
    assert_eq!(machine["state"], "error");
    assert_eq!(machine["reason"], "no_local_network");
}

#[test]
fn watch_emits_snapshots_and_reloads_config() {
    use std::io::{BufRead, BufReader};
    use std::process::Stdio;

    let wol = Wol::new();
    wol.ok(&["add", "Local", "--mac", "001122334455"]);

    let mut child = std::process::Command::new(assert_cmd::cargo::cargo_bin("wol"))
        .arg("--config")
        .arg(wol.config())
        .args(["watch", "--json", "--interval", "1"])
        .env("WOL_FAKE_SCAN", "")
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let mut lines = BufReader::new(child.stdout.take().unwrap()).lines();
    let mut next_event = || serde_json::from_str::<Value>(&lines.next().unwrap().unwrap()).unwrap();

    let first = next_event();
    assert_eq!(first["schema"], 1);
    assert_eq!(first["event"], "snapshot");
    assert_eq!(first["machines"].as_array().unwrap().len(), 1);

    // Editing the file while watching produces a new snapshot including the new machine.
    wol.ok(&["add", "Other", "--mac", "001122334456"]);
    let second = next_event();
    child.kill().unwrap();
    child.wait().unwrap();

    assert_eq!(second["event"], "snapshot");
    assert_eq!(second["machines"].as_array().unwrap().len(), 2);
}

#[test]
fn shared_mac_addresses_are_allowed_with_a_warning() {
    let wol = Wol::new();
    wol.ok(&["add", "VM 1", "--mac", "001122334455"]);

    let (code, stdout, _) = wol.json(&["add", "VM 2", "--mac", "001122334455"]);
    assert_eq!(code, 0);
    let warning = &stdout.unwrap()["warnings"][0];
    assert_eq!(warning["code"], "shared_mac");
    assert_eq!(warning["mac"], "00:11:22:33:44:55");
    assert_eq!(warning["machines"], serde_json::json!(["VM 1", "VM 2"]));

    let (_, stdout, _) = wol.json(&["list"]);
    let stdout = stdout.unwrap();
    assert_eq!(stdout["machines"].as_array().unwrap().len(), 2);
    assert_eq!(stdout["warnings"].as_array().unwrap().len(), 1);

    wol.command(&["list"])
        .assert()
        .success()
        .stdout(predicates::str::contains(
            "Warning: MAC address 00:11:22:33:44:55 is used by several machines (\"VM 1\", \"VM 2\")",
        ));
}

#[test]
fn scan_lists_devices_and_known_machines() {
    let wol = Wol::new().network("192.168.1.30=66:77:88:99:AA:BB, 192.168.1.20=00:11:22:33:44:55");
    wol.ok(&["add", "Desk", "--mac", "001122334455=Wi-Fi"]);

    let (code, stdout, _) = wol.json(&["scan"]);
    assert_eq!(code, 0);
    let devices = stdout.unwrap()["devices"].as_array().unwrap().clone();
    assert_eq!(devices.len(), 2);
    assert_eq!(devices[0]["address"], "192.168.1.20");
    assert_eq!(
        devices[0]["machines"],
        serde_json::json!([{ "machine": "Desk", "label": "Wi-Fi" }])
    );
    assert_eq!(devices[1]["mac"], "66:77:88:99:AA:BB");
    assert_eq!(devices[1]["machines"], serde_json::json!([]));

    let (code, _, stderr) = Wol::new().network("none").json(&["scan"]);
    assert_eq!(code, 5);
    assert_eq!(stderr.unwrap()["error"]["code"], "no_local_network");
}

#[test]
fn wake_wait_reports_machines_coming_online() {
    let wol = Wol::new().network("192.168.1.20=00:11:22:33:44:55");
    wol.ok(&[
        "add",
        "Desk",
        "--mac",
        "001122334455=Wi-Fi",
        "--broadcast",
        "127.0.0.1",
    ]);

    let (code, events) = wol.ndjson(&["wake", "desk", "--wait"]);
    assert_eq!(code, 0);
    let kinds: Vec<&str> = events
        .iter()
        .map(|event| event["event"].as_str().unwrap())
        .collect();
    assert_eq!(kinds, ["sent", "online", "finished"]);
    assert_eq!(events[0]["schema"], 1);
    assert_eq!(events[0]["timeout_s"], 120);
    assert_eq!(events[0]["config"]["status"], "loaded");
    assert_eq!(events[0]["results"][0]["status"], "sent");
    assert_eq!(events[1]["machine"]["address"], "192.168.1.20");
    assert_eq!(events[1]["machine"]["label"], "Wi-Fi");
    assert_eq!(events[2]["online"], serde_json::json!(["Desk"]));
}

#[test]
fn wake_wait_times_out() {
    let wol = Wol::new();
    wol.ok(&[
        "add",
        "Desk",
        "--mac",
        "001122334455",
        "--broadcast",
        "127.0.0.1",
    ]);

    let (code, events) = wol.ndjson(&["wake", "Desk", "--wait=1"]);
    assert_eq!(code, 6);
    let kinds: Vec<&str> = events
        .iter()
        .map(|event| event["event"].as_str().unwrap())
        .collect();
    assert_eq!(kinds, ["sent", "timed_out", "finished"]);
    assert_eq!(events[1]["machine"], "Desk");
    assert_eq!(events[2]["timed_out"], serde_json::json!(["Desk"]));

    wol.command(&["wake", "Desk", "--wait=1"])
        .assert()
        .code(6)
        .stdout(predicates::str::contains(
            "\"Desk\" did not come online within 1 s.",
        ));
}
