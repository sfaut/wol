# CLI contract

> **Status: draft, partially implemented.** This document is the single source of truth for
> the interface between the `wol` CLI and any front-end (the Windows GUI today, a macOS GUI or
> a `wol serve` service later). Any change to the JSON output must be reflected here first.
>
> Implemented: `list`, `wake`, `add`, `edit`, `remove`, `group …`.
> Not implemented yet: `status`, `watch`, `wake --wait`, `--lang`, localized output.

## Configuration file: `wol.toml`

Lookup order (first match wins):

1. `--config <path>` option.
2. `wol.toml` next to the `wol` executable (portable mode), if it exists.
3. User configuration directory:
   - Windows: `%APPDATA%\wol\wol.toml`
   - Linux: `$XDG_CONFIG_HOME/wol/wol.toml` (default `~/.config/wol/wol.toml`)
   - macOS: `~/Library/Application Support/wol/wol.toml`

A missing file is an empty configuration; it is created on the first change.
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
| `wol wake [<machine>…] [--group <name>]…` | Send magic packets (a machine named twice is woken once) |
| `wol add <name> --mac <mac> --host <host> [--group <g>] [--broadcast <ipv4>] [--port <p>] [--check-port <p>]…` | Add a machine |
| `wol edit <name> [--name <new>] [--mac …] [--host …] [--group <g> \| --no-group] [--broadcast <ipv4> \| --no-broadcast] [--port <p> \| --no-port] [--check-port <p>… \| --no-check-ports]` | Change a machine; only given options change, `--check-port` replaces the list |
| `wol remove <name>` | Remove a machine |
| `wol group add <name>` | Add a group at the end of the list |
| `wol group rename <name> <new-name>` | Rename a group; its machines follow |
| `wol group remove <name> [--ungroup]` | Remove a group; fails if not empty unless `--ungroup` |
| `wol group move <name> <position>` | Move a group to a 1-based position (clamped) |
| `wol status [<machine>…] [--group <name>]` | *(planned)* Check current state once |
| `wol watch [--interval <s>]` | *(planned)* Monitor continuously, one event per state change |

## JSON output

### General rules

- Every JSON document starts with `"schema": <integer>` (currently `1`), bumped on any breaking change.
- JSON is **language-neutral**: states, statuses and errors are stable `snake_case` codes.
  Front-ends translate them; human-readable output may be localized, JSON never is.
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

### Per command

| Command | `stdout` document |
|---|---|
| `list` | `{ "schema", "config": "<path>", "groups": [Group…], "machines": [Machine…] }` |
| `wake` | `{ "schema", "results": [{ "machine", "target": "<ip>:<port>", "status": "sent" \| "failed", "error"?: { "code", "message" } }…] }` |
| `add`, `edit`, `remove` | `{ "schema", "machine": Machine }` (the removed machine for `remove`) |
| `group add`, `group move` | `{ "schema", "group": Group, "position": <1-based> }` |
| `group rename` | `{ "schema", "group": Group }` |
| `group remove` | `{ "schema", "group": Group, "ungrouped": ["<machine>"…] }` |

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

## Machine states *(planned, for `status` and `watch`)*

`online`, `offline`, `waking`, `timed_out`, `unknown`, `error`.

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
