# CLI contract

> **Status: draft, partially implemented.** This document is the single source of truth for
> the interface between the `wol` CLI and any front-end (the Windows GUI today, a macOS GUI or
> a `wol serve` service later). Any change to the JSON output must be reflected here first.
>
> Implemented: `list`, `status`, `wake`, `add`, `edit`, `remove`, `group …`.
> Not implemented yet: `watch`, `wake --wait`, `--lang`, localized output.

## Configuration file: `wol.toml`

Lookup order (first match wins):

1. `--config <path>` option.
2. `wol.toml` next to the `wol` executable (portable mode), if it exists.
3. User configuration directory:
   - Windows: `%APPDATA%\wol\wol.toml`
   - Linux: `$XDG_CONFIG_HOME/wol/wol.toml` (default `~/.config/wol/wol.toml`)
   - macOS: `~/Library/Application Support/wol/wol.toml`

A missing file is an empty configuration; it is created on the first change.
Every command reports which file it used and what happened to it: on `stderr` in
human-readable mode (`Configuration: <path> (created)`), in the `config` object in JSON.
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
mac = "A4:BB:6D:12:34:56"        # also accepts AA-BB-…, AABB.CCDD.EEFF, AABBCCDDEEFF
host = "192.168.1.20"            # IP address or host name used for status checks
broadcast = "192.168.1.255"      # optional, default: 255.255.255.255
port = 9                         # optional UDP port of the magic packet, default: 9
check_ports = [3389, 445]        # optional TCP fallback when ICMP is unavailable
```

Rules:

- Names (machines and groups): 1 to 64 characters, no leading or trailing spaces,
  unique **case-insensitively**. Commands also match names case-insensitively.
- `host`: at most 253 characters, no spaces.
- Ports: 1 to 65535.
- `group` must name a defined `[[group]]`.
- Unknown keys are rejected (they are most likely typos).

## Commands

Global options: `--json` (machine-readable output), `--config <path>`.

| Command | Purpose |
|---|---|
| `wol list` | List groups and machines |
| `wol status [<machine>…] [--group <name>]… [--timeout <ms>]` | Check the state of machines once (all machines by default) |
| `wol watch [<machine>…] [--group <name>]… [--interval <s>] [--timeout <ms>]` | *(planned)* Monitor machines continuously, one event per state change (all machines by default) |
| `wol wake [<machine>…] [--group <name>]…` | Send magic packets (a machine named twice is woken once) |
| `wol add <name> --mac <mac> --host <host> [--group <g>] [--broadcast <ipv4>] [--port <p>] [--check-port <p>]…` | Add a machine |
| `wol edit <name> [--name <new>] [--mac …] [--host …] [--group <g> \| --no-group] [--broadcast <ipv4> \| --no-broadcast] [--port <p> \| --no-port] [--check-port <p>… \| --no-check-ports]` | Change a machine; only given options change, `--check-port` replaces the list |
| `wol remove <name>` | Remove a machine |
| `wol group add <name>` | Add a group at the end of the list |
| `wol group rename <name> <new-name>` | Rename a group; its machines follow |
| `wol group remove <name> [--ungroup]` | Remove a group; fails if not empty unless `--ungroup` |
| `wol group move <name> <position>` | Move a group to a 1-based position (clamped) |

## State checks

`status` checks machines concurrently. For each machine:

1. Resolve `host` (an IPv4 address is preferred). Failure: state `error`, reason `host_not_found`.
2. Send one ICMP echo request, without administrator rights (Windows `IcmpSendEcho`; unprivileged
   ICMP sockets on Linux / macOS, which on Linux require `net.ipv4.ping_group_range` to include
   the user's group). A reply: state `online`, method `icmp`.
3. Otherwise, try each `check_ports` entry with a TCP connection. Accepted **or refused** (the
   host answered with a reset): state `online`, method `tcp`.
4. Otherwise: state `offline`; or `unknown` with reason `icmp_unavailable` when ICMP cannot be used
   (permissions, IPv6 host) and no TCP port is configured.

`--timeout` (default 1000 ms, 100 to 60000) applies to the ICMP attempt and to each TCP attempt.

## JSON output

### General rules

- Every JSON document starts with `"schema": <integer>` (currently `1`), bumped on any breaking change.
- JSON is **language-neutral**: states, statuses and errors are stable `snake_case` codes.
  Front-ends translate them; human-readable output may be localized, JSON never is.
- Then comes `"config": { "path": "<absolute path>", "status": "<status>" }`, where status is
  `loaded` (read, unchanged), `updated` (rewritten), `created` (written for the first time) or
  `missing` (does not exist, empty configuration). It is present in every success document and
  in error documents raised once the file is loaded; errors loading the file name it in `details`.
- Successful commands print one JSON document on `stdout`.
- `watch` will print **NDJSON** (one JSON document per line) on `stdout`, flushed per event.
- Errors print one JSON document on `stderr` and nothing on `stdout`.

### Objects

Machine (optional fields are `null` when not set, meaning "default"):

```json
{ "name": "Office PC", "group": "Office", "mac": "A4:BB:6D:12:34:56", "host": "192.168.1.20",
  "broadcast": null, "port": null, "check_ports": [3389] }
```

Group: `{ "name": "Office" }`

Machine state (fields that do not apply are `null`):

```json
{ "machine": "Office PC", "state": "online", "address": "192.168.1.20",
  "method": "icmp", "port": null, "latency_ms": 3, "reason": null }
```

- `state`: see "Machine states".
- `address`: address `host` resolved to; `null` when it did not resolve.
- `method`: `icmp` or `tcp` when online; `port`: the TCP port that answered.
- `latency_ms`: ICMP round-trip time or TCP connection time, when online.
- `reason`: `host_not_found` (state `error`) or `icmp_unavailable` (state `unknown`).

### Per command

| Command | `stdout` document |
|---|---|
| `list` | `{ "schema", "config", "groups": [Group…], "machines": [Machine…] }` |
| `status` | `{ "schema", "config", "checked_at": "<RFC 3339 UTC>", "machines": [Machine state…] }` |
| `wake` | `{ "schema", "config", "results": [{ "machine", "target": "<ip>:<port>", "status": "sent" \| "failed", "error"?: { "code", "message" } }…] }` |
| `add`, `edit`, `remove` | `{ "schema", "config", "machine": Machine }` (the removed machine for `remove`) |
| `group add`, `group move` | `{ "schema", "config", "group": Group, "position": <1-based> }` |
| `group rename` | `{ "schema", "config", "group": Group }` |
| `group remove` | `{ "schema", "config", "group": Group, "ungrouped": ["<machine>"…] }` |

`wake` reports send failures per machine and still prints its document; the exit code is then 5.

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
| `network_error` | (per machine, inside `wake` results) | 5 |

## Machine states

`online`, `offline`, `unknown`, `error` (see "State checks").
Planned for `wake --wait`: `waking`, `timed_out`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Generic failure (I/O) |
| 2 | Invalid usage (bad arguments or values, name conflicts) |
| 3 | Configuration file invalid |
| 4 | Machine or group not found |
| 5 | Network error |
| 6 | *(planned)* Wake timed out |

## Contract tests

`cli/tests/cli.rs` checks this contract end to end. JSON Schemas (`docs/schema/`) and sample
outputs (`docs/fixtures/`) are planned, to be shared with the GUI test suite.
