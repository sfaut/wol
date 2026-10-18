# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Wol is a Wake-on-LAN machine manager: users keep a list of remote machines organised in
groups, monitor their state, and wake them up with magic packets. It is made of two
independent applications sharing one contract:

- `cli/`: the `wol` CLI, in Rust. Portable to Windows 10 / 11, Linux and macOS (x86_64 and ARM).
  It owns **all** the logic: magic packets, state checks, the `wol.toml` configuration file.
- `gui/`: the Windows desktop app, in C# / .NET 10 / WinUI 3 (project `Wol.Desktop`,
  executable `wol-gui.exe`). Windows only.
  It is a front-end that **drives the CLI** and never re-implements its logic.
- `docs/cli-contract.md`: the contract between them (config file, commands, JSON output,
  exit codes). Update it **before** changing any JSON output.
- `docs/design/gui-brief.md`: the GUI design brief (screens, states, controls).

Everything in the codebase is in English: identifiers, comments, docs, commit messages.

## Commands

CLI (run from `cli/`):

```sh
cargo build                      # debug build
cargo build --release            # optimized build (LTO, stripped, panic = abort)
cargo run -- <args>              # run the CLI, e.g. cargo run -- status --json
cargo test                       # all tests (unit tests in src/, end-to-end tests in tests/cli.rs)
cargo test <test_name>           # tests whose name contains <test_name>
cargo test --test cli <name>     # end-to-end tests only
cargo fmt                        # format
cargo clippy --all-targets -- -D warnings
```

GUI (run from `gui/`, solution `Wol.slnx`):

```sh
dotnet build
dotnet run --project src/Wol.Desktop
dotnet publish src/Wol.Desktop -c Release -r win-x64   # or win-arm64
```

There is no GUI test project yet.

## Architecture

### CLI layout

The crate is a library plus a thin binary (`src/main.rs` calls `cli::run`), so end-to-end
tests and future front-ends (`wol serve`) reuse the same code.

- `cli.rs`: clap definitions, command execution, human / JSON output. Each command returns an
  `Outcome` (JSON value, text, exit code); `run` prints it and adds the `schema` field.
- `config.rs`: `wol.toml` model and edits. Reads with `toml` (typed, validated model) and edits
  with `toml_edit` (`DocumentMut`) so hand-written comments survive. Every mutation edits the
  document, then `refresh()` re-parses and re-validates it: the file on disk can never become
  invalid through the CLI. Machine and group indexes in the model match the `[[machine]]` /
  `[[group]]` array indexes in the document.
- `error.rs`: one `Error` enum; each variant maps to a stable JSON `code`, `details` and an exit code.
- `mac.rs`, `magic.rs`: MAC address parsing, magic packet building and sending.
- `icmp.rs`, `probe.rs`: state checks. `icmp.rs` holds the only platform-specific code
  (`#[cfg(windows)]` `IcmpSendEcho` via `windows-sys`, `#[cfg(unix)]` ICMP datagram sockets via
  `socket2`); `probe.rs` resolves the host, pings, falls back to TCP, and runs one thread per
  machine (`std::thread::scope`, no async runtime).
- `watch.rs`: the `watch` loop (config reload on file change, debounced state tracking, NDJSON
  events). `time.rs`: RFC 3339 timestamps without a date library.

Platform-specific code cannot run here: after touching it, at least lint the other targets with
`cargo clippy --all-targets --target x86_64-unknown-linux-gnu` and `--target aarch64-apple-darwin`.

### The GUI drives the CLI

- The GUI ships `wol.exe` next to its own executable and runs it with `System.Diagnostics.Process`.
- One-shot commands (`list`, `status`, `wake`, `add`, `edit`, `remove`, `group …`) are run with
  `--json`; continuous monitoring uses a single long-lived `wol watch --json` process whose
  `stdout` is NDJSON (one event per line).
- The GUI never reads or writes `wol.toml`: every change goes through CLI commands.
- A `wol serve` mode (same JSON over a named pipe or the network) is planned, so the GUI can
  later drive a CLI running on another machine. Keep the JSON shapes transport-agnostic.

### JSON contract rules

- Every JSON document carries `"schema": <integer>`; bump it on any breaking change.
- JSON is language-neutral: states, events and errors are stable `snake_case` codes.
  Only human-readable output is localized; front-ends translate codes themselves.
- Errors go to `stderr` (JSON with `--json`), with documented exit codes.
- JSON Schemas (`docs/schema/`) and sample outputs (`docs/fixtures/`) are meant to be shared by
  both test suites (CLI produces them, GUI parses them).

### Configuration lookup

`--config <path>`, then `wol.toml` next to the executable (portable mode), then the user
config directory (`%APPDATA%\wol\`, `~/.config/wol/`, `~/Library/Application Support/wol/`).

### State checks

ICMP ping without elevated privileges (Windows: `IcmpSendEcho`; Linux / macOS: unprivileged
ICMP datagram sockets), with an optional TCP-connect fallback on configured ports.

## Constraints

Minimal system footprint is a core requirement:

- No registry writes, no installer, no system-wide runtime. Both apps are portable.
- CLI on Windows links the C runtime statically (`cli/.cargo/config.toml`, `+crt-static`);
  on Linux, target musl for static binaries.
- GUI is unpackaged (`WindowsPackageType=None`), self-contained Windows App SDK, Native AOT.
  Keep code AOT-compatible: no reflection-based serialization (use `System.Text.Json`
  source generation).
- GUI notifications use `Shell_NotifyIcon` balloons (tray icon), never `AppNotificationManager`
  (it registers the app in the registry). "Start with Windows" is an opt-in shortcut in
  `shell:startup`.
- Executable names must differ case-insensitively (`wol.exe` and `wol-gui.exe` share a folder).
- Development uses VS Code only (no Visual Studio IDE, no JetBrains); everything must build
  from the command line.

## Localization

English (default and fallback) and French.

- CLI (not implemented yet; output is English only): Project Fluent files embedded at compile
  time; language from `--lang`, then `[settings] language` in `wol.toml`, then the system locale.
- GUI: `.resw` files in `gui/src/Wol.Desktop/Strings/<locale>/Resources.resw`, referenced
  with `x:Uid` in XAML or `ResourceLoader` in code. Layouts must tolerate ~30% longer French strings.
