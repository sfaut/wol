# CLI contract

> **Status: draft, partially implemented.** This document is the single source of truth for
> the interface between the `wol` CLI and any front-end (the Windows GUI today, a macOS GUI or
> a `wol serve` service later). Any change to the JSON output must be reflected here first.
>
> Implemented: `list`, `status`, `watch`, `scan`, `wake` (with `--wait`), `version`, `add`, `edit`, `remove`, `group …`.
> Not implemented yet: `--lang`, localized output.

## Configuration file: `wol.toml`

Lookup order (first match wins):

1. `--config <path>` option.
2. `wol.toml` next to the `wol` executable (portable mode), if it exists.
3. User configuration directory:
   - Windows: `%APPDATA%\wol\wol.toml`
   - Linux: `$XDG_CONFIG_HOME/wol/wol.toml` (default `~/.config/wol/wol.toml`)
   - macOS: `~/Library/Application Support/wol/wol.toml`

A missing file is an empty configuration; it is created on the first change.
In JSON, every command reports which file it used and what happened to it, in the `config`
object. For humans, `wol` without a command prints its help summary with the file in use
(`Configuration: <path>`, followed by `(not created yet)` when it does not exist).
Only the CLI reads and writes this file: front-ends go through the CLI commands. The CLI
preserves comments and layout written by hand.

```toml
# Optional CLI settings.
[settings]
language = "en"        # "en" or "fr"; default: system language, fallback "en"

# Groups, in display order.
[[group]]
name = "Office"

[[group]]
name = "Servers"

[[machine]]
name = "Office PC"
group = "Office"                 # optional, at most one group
interfaces = [                   # 1 to N network interfaces
    { mac = "A4:BB:6D:12:34:56", label = "Ethernet" },
    { mac = "A4:BB:6D:12:34:57", label = "Wi-Fi" },   # label: optional
]
broadcast = "192.168.1.255"      # optional, default: 255.255.255.255
port = 9                         # optional UDP port of the magic packet, default: 9

[[machine]]
name = "NAS"
interfaces = [{ mac = "00:11:32:BC:72:E9" }]
```

Rules:

- Names (machines and groups): 1 to 64 characters, no leading or trailing spaces,
  unique **case-insensitively**. Commands also match names case-insensitively.
- `interfaces`: at least one. MAC addresses accept `AA:BB:CC:DD:EE:FF`, `AA-BB-…`,
  `AABB.CCDD.EEFF` and `AABBCCDDEEFF`, and are written as `AA:BB:CC:DD:EE:FF`.
  A MAC address appears at most once per machine.
- The same MAC address may be used by several machines (e.g. cloned virtual machines): allowed,
  but reported as a `shared_mac` warning, since these machines cannot be told apart when that
  address answers.
- `label`: 1 to 32 characters, no leading or trailing spaces.
- Ports: 1 to 65535.
- `group` must name a defined `[[group]]`.
- Unknown keys are rejected (they are most likely typos).

## Commands

Global options: `--json` (machine-readable output), `--config <path>`.
`wol --help` and `wol <command> --help` explain every option.

| Command | Purpose |
|---|---|
| `wol list` | List groups and machines |
| `wol status [<machine>…] [--group <name>]…` | Check the state of machines once (all machines by default) |
| `wol watch [<machine>…] [--group <name>]… [--interval <s>]` | Monitor machines continuously, one event per state change (all machines by default) |
| `wol scan` | List the devices found on the local network |
| `wol wake [<machine>…] [--group <name>]… [--wait[=<s>]]` | Send magic packets to every interface (a machine named twice is woken once); with `--wait`, wait until the machines are online (default 120 s, 1 to 3600) |
| `wol add <name> --mac <mac[=label]>… [--group <g>] [--broadcast <ipv4>] [--port <p>]` | Add a machine; `--mac` is repeatable, at least one |
| `wol edit <name> [--name <new>] [--mac <mac[=label]>… \| [--add-mac <mac[=label]>]… [--remove-mac <mac>]…] [--group <g> \| --no-group] [--broadcast <ipv4> \| --no-broadcast] [--port <p> \| --no-port]` | Change a machine; only given options change. `--mac` replaces all interfaces; `--add-mac` adds one, or replaces the label of an existing one |
| `wol remove <name>` | Remove a machine |
| `wol version` | Print the CLI version and the JSON schema version; reads no configuration file |
| `wol group add <name>` | Add a group at the end of the list |
| `wol group rename <name> <new-name>` | Rename a group; its machines follow |
| `wol group remove <name> [--ungroup]` | Remove a group; fails if not empty unless `--ungroup` |
| `wol group move <name> <position>` | Move a group to a 1-based position (clamped) |

## Local network scan

Wol works on the **local network only**. `status`, `watch` and `scan` scan it, without
administrator rights, to find which MAC addresses are present and at which IPv4 address:

- Scanned subnets: those of the connected network interfaces. On Windows, interfaces with a
  default gateway are preferred (the others are usually virtual switches: Hyper-V, WSL, VPN).
  Subnets larger than /22 are reduced to the /24 around the interface's own address.
- Windows: an ARP request (`SendARP`) to every address, answers collected for 1.5 s.
- Linux: a UDP datagram to every address makes the kernel resolve it, then the neighbor table
  is read (netlink); only entries the kernel just confirmed count. Up to about 8 s.
- macOS: the same trigger, then the ARP table is read (`sysctl`) after 1.5 s. macOS does not
  tell whether an entry is fresh: a machine that just went off may still appear online until
  its entry expires (about 20 minutes).

A machine is `online` when **any** of its interfaces answers; the first answering interface,
in configuration order, is reported with its current IP address. When a MAC address answers on
several IP addresses, the lowest one is kept. Without any connected local network, machines are
in the `error` state with reason `no_local_network`.

## JSON output

### General rules

- Every JSON document starts with `"schema": <integer>` (currently `1`), bumped on any breaking change.
- JSON is **language-neutral**: states, statuses and errors are stable `snake_case` codes.
  Front-ends translate them; human-readable output may be localized, JSON never is.
- Then comes `"config": { "path": "<absolute path>", "status": "<status>" }`, where status is
  `loaded` (read, unchanged), `updated` (rewritten), `created` (written for the first time) or
  `missing` (does not exist, empty configuration). It is present in every success document and
  in error documents raised once the file is loaded; errors loading the file name it in `details`.
  `watch` events carry it in `snapshot` events only.
- Successful commands print one JSON document on `stdout`.
- `watch` prints **NDJSON** (one JSON document per line) on `stdout`, flushed per event.
- Errors print one JSON document on `stderr` and nothing on `stdout`.

### Objects

Machine (optional fields are `null` when not set, meaning "default"):

```json
{ "name": "Office PC", "group": "Office",
  "interfaces": [{ "mac": "A4:BB:6D:12:34:56", "label": "Ethernet" },
                 { "mac": "A4:BB:6D:12:34:57", "label": null }],
  "broadcast": null, "port": null }
```

Group: `{ "name": "Office" }`

Machine state (fields that do not apply are `null`):

```json
{ "machine": "Office PC", "state": "online", "mac": "A4:BB:6D:12:34:57", "label": "Wi-Fi",
  "address": "192.168.1.35", "reason": null }
```

- `state`: see "Machine states".
- `mac`, `label`: interface that answered, when online.
- `address`: current IPv4 address of the machine, when online.
- `reason`: `no_local_network` or `scan_failed` (state `error`).

Warning:

```json
{ "code": "shared_mac", "message": "MAC address 00:11:22:33:44:55 is used by several machines (\"VM 1\", \"VM 2\"), which cannot be told apart when it answers",
  "mac": "00:11:22:33:44:55", "machines": ["VM 1", "VM 2"] }
```

### Per command

| Command | `stdout` document |
|---|---|
| `list` | `{ "schema", "config", "groups": [Group…], "machines": [Machine…], "warnings": [Warning…] }` |
| `status` | `{ "schema", "config", "checked_at": "<RFC 3339 UTC>", "machines": [Machine state…] }` |
| `watch` | NDJSON events, see below |
| `scan` | `{ "schema", "config", "scanned_at": "<RFC 3339 UTC>", "devices": [{ "address", "mac", "machines": [{ "machine", "label" }…] }…] }`, sorted by address; `machines` is empty for unknown devices |
| `wake` | `{ "schema", "config", "results": [{ "machine", "target": "<ip>:<port>", "macs": ["<mac>"…], "status": "sent" \| "failed", "error"?: { "code", "message" } }…] }` |
| `wake --wait` | NDJSON events, see below |
| `add`, `edit` | `{ "schema", "config", "machine": Machine, "warnings": [Warning…] }` (warnings about the MAC addresses of this machine) |
| `remove` | `{ "schema", "config", "machine": Machine }` (the removed machine) |
| `version` | `{ "schema", "version": "<CLI version>" }` (no `config`: the file is not read). Front-ends check `schema` to detect an incompatible CLI |
| `group add`, `group move` | `{ "schema", "config", "group": Group, "position": <1-based> }` |
| `group rename` | `{ "schema", "config", "group": Group }` |
| `group remove` | `{ "schema", "config", "group": Group, "ungrouped": ["<machine>"…] }` |

`wake` reports send failures per machine and still prints its document; the exit code is then 5.
`status` still prints its document when the scan fails (machines in the `error` state); the exit
code is then 5.

### `watch` events

`watch` runs until it is stopped (and exits quietly when its `stdout` is closed). It scans the
network every `--interval` seconds (default 10, 1 to 3600, measured start to start) and
re-reads the configuration file whenever it changes on disk.

| `event` | Fields | When |
|---|---|---|
| `snapshot` | `config`, `checked_at`, `interval_s`, `machines`: [Machine state…] | At start, and after each configuration reload (front-ends replace their whole state) |
| `state_changed` | `checked_at`, `previous`: state, `machine`: Machine state | A machine changed state |
| `error` | `error`: { `code`, `message`, `details` } | The edited file is invalid, or no longer contains a watched machine or group; the previous configuration stays in use |

```json
{"schema":1,"event":"state_changed","checked_at":"2026-10-18T12:37:23.037Z","previous":"offline","machine":{"machine":"NAS","state":"online","mac":"00:11:32:BC:72:E9","label":null,"address":"192.168.1.30","reason":null}}
```

Leaving `online` is reported only after 2 consecutive scans agree, so that a single missed
answer does not trigger a notification; every other change is reported at once.

### `wake --wait` events

After sending the magic packets, `wake --wait` scans the local network every 2 seconds until
every machine whose packets were sent is online, or the time is up.

| `event` | Fields | When |
|---|---|---|
| `sent` | `config`, `timeout_s`, `results`: [same as `wake`] | Once, after sending |
| `online` | `elapsed_s`, `machine`: Machine state | A machine came online (seconds since sending) |
| `error` | `error`: { `code`, `message`, `details` } | The local network cannot be scanned (reported once; scans go on until the time is up) |
| `timed_out` | `elapsed_s`, `machine`: "<name>" | A machine was still offline when the time was up |
| `finished` | `online`, `timed_out`, `failed`: ["<machine>"…] | Last event |

Exit code: 5 if a send failed, otherwise 6 if a machine timed out, otherwise 0.

### Errors

```json
{ "schema": 1, "error": { "code": "machine_not_found", "message": "machine not found: NAS", "details": { "machine": "NAS" } } }
```

`message` is an English fallback; front-ends rely on `code` and `details`.

| `code` | `details` | Exit code |
|---|---|---|
| `invalid_usage` | `{}` | 2 |
| `invalid_value` | `{ "field" }` | 2 |
| `machine_already_exists` | `{ "machine" }` | 2 |
| `group_already_exists` | `{ "group" }` | 2 |
| `group_not_empty` | `{ "group", "machines": [...] }` | 2 |
| `config_invalid` | `{ "path", "line" }` (`line` may be `null`) | 3 |
| `machine_not_found` | `{ "machine" }` | 4 |
| `group_not_found` | `{ "group" }` | 4 |
| `io_error` | `{ "path" }` | 1 |
| `no_local_network` | `{}` (`scan`) | 5 |
| `scan_failed` | `{}` (`scan`) | 5 |
| `network_error` | (per machine, inside `wake` results) | 5 |

## Machine states

`online`, `offline`, `error` (see "Local network scan").
Front-ends add `unknown` (not checked yet) on their side.
Front-ends show `waking` while `wake --wait` runs, and `timed_out` after a `timed_out` event.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Generic failure (I/O) |
| 2 | Invalid usage (bad arguments or values, name conflicts) |
| 3 | Configuration file invalid |
| 4 | Machine or group not found |
| 5 | Network error (including no local network to scan) |
| 6 | Machines did not come online in time (`wake --wait`) |

## Contract tests

`cli/tests/cli.rs` checks this contract end to end. The variable `WOL_FAKE_SCAN` replaces the
network scan in tests: `IP=MAC` pairs separated by commas, or `none` for no local network.
JSON Schemas (`docs/schema/`) and sample outputs (`docs/fixtures/`) are planned, to be shared
with the GUI test suite.
