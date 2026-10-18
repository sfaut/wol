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
}

impl Wol {
    /// Creates an empty test environment.
    fn new() -> Self {
        Self {
            dir: TempDir::new().unwrap(),
        }
    }

    /// Path of the configuration file.
    fn config(&self) -> PathBuf {
        self.dir.path().join("wol.toml")
    }

    /// Builds a `wol` command using the test configuration file.
    fn command(&self, args: &[&str]) -> Command {
        let mut command = Command::cargo_bin("wol").unwrap();
        command.arg("--config").arg(self.config()).args(args);
        command
    }

    /// Runs a command that must succeed.
    fn ok(&self, args: &[&str]) {
        self.command(args).assert().success();
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
        "a4-bb-6d-12-34-56",
        "--host",
        "192.168.1.20",
        "--group",
        "office",
        "--check-port",
        "3389",
    ]);

    let (_, stdout, _) = wol.json(&["list"]);
    let machine = &stdout.unwrap()["machines"][0];
    assert_eq!(machine["name"], "Office PC");
    assert_eq!(machine["group"], "Office");
    assert_eq!(machine["mac"], "A4:BB:6D:12:34:56");
    assert_eq!(machine["broadcast"], Value::Null);
    assert_eq!(machine["check_ports"], serde_json::json!([3389]));

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
    wol.ok(&["add", "NAS", "--mac", "001122334455", "--host", "nas.local"]);

    let cases: [(&[&str], i32, &str); 5] = [
        (&["wake", "Ghost"], 4, "machine_not_found"),
        (&["wake", "--group", "Ghost"], 4, "group_not_found"),
        (
            &["add", "nas", "--mac", "001122334455", "--host", "x"],
            2,
            "machine_already_exists",
        ),
        (
            &["add", "PC", "--mac", "nope", "--host", "x"],
            2,
            "invalid_usage",
        ),
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
        "--host",
        "nas",
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

    let mut buffer = [0u8; 256];
    let (len, _) = receiver.recv_from(&mut buffer).unwrap();
    assert_eq!(len, 102);
    assert_eq!(&buffer[6..12], &[0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
}

#[test]
fn group_lifecycle() {
    let wol = Wol::new();
    wol.ok(&["group", "add", "Office"]);
    wol.ok(&["group", "add", "Servers"]);
    wol.ok(&[
        "add",
        "PC",
        "--mac",
        "001122334455",
        "--host",
        "pc",
        "--group",
        "Office",
    ]);

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
    let listener = std::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
    let port = listener.local_addr().unwrap().port().to_string();

    let wol = Wol::new();
    wol.ok(&[
        "add",
        "Local",
        "--mac",
        "001122334455",
        "--host",
        "127.0.0.1",
        "--check-port",
        &port,
    ]);
    wol.ok(&[
        "add",
        "Nowhere",
        "--mac",
        "001122334456",
        "--host",
        "nowhere.invalid",
    ]);

    let (code, stdout, _) = wol.json(&["status"]);
    assert_eq!(code, 0);
    let stdout = stdout.unwrap();
    assert!(stdout["checked_at"].as_str().unwrap().ends_with('Z'));

    let machines = stdout["machines"].as_array().unwrap();
    assert_eq!(machines[0]["machine"], "Local");
    assert_eq!(machines[0]["state"], "online");
    assert!(machines[0]["latency_ms"].is_u64());
    assert_eq!(machines[1]["state"], "error");
    assert_eq!(machines[1]["reason"], "host_not_found");

    let (_, stdout, _) = wol.json(&["status", "nowhere"]);
    assert_eq!(stdout.unwrap()["machines"].as_array().unwrap().len(), 1);
}

#[test]
fn watch_emits_snapshots_and_reloads_config() {
    use std::io::{BufRead, BufReader};
    use std::process::Stdio;

    let wol = Wol::new();
    wol.ok(&[
        "add",
        "Local",
        "--mac",
        "001122334455",
        "--host",
        "127.0.0.1",
    ]);

    let mut child = std::process::Command::new(assert_cmd::cargo::cargo_bin("wol"))
        .arg("--config")
        .arg(wol.config())
        .args(["watch", "--json", "--interval", "1", "--timeout", "300"])
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
    wol.ok(&[
        "add",
        "Other",
        "--mac",
        "001122334456",
        "--host",
        "127.0.0.1",
    ]);
    let second = next_event();
    child.kill().unwrap();
    child.wait().unwrap();

    assert_eq!(second["event"], "snapshot");
    assert_eq!(second["machines"].as_array().unwrap().len(), 2);
}
