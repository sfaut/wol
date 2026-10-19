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
- `docs/DEVELOPER.md`: prerequisites, build, tests, CI and releases, for developers; the
  `README.md` is for end users (installation, desktop app and CLI usage).
- `design/`: the GUI design from Claude Design (mockups, HTML boards, specs in `design/specs/`,
  icons) and the brief it was made from (`design/brief.md`).

Everything in the codebase is in English: identifiers, comments, docs, commit messages.

## Status

v1.0.0 was released on 2026-10-19 (GitHub release with the CLI for Windows, Linux and macOS,
x64 and ARM64, and the desktop app for Windows x64). History of the versions:
`docs/CHANGELOG.md`, newest first.

Known gaps and planned work:

- The desktop app is published for Windows x64 only: `-r win-arm64` still copies the CLI and
  the launcher from `target/release` (x64), so the csproj and the CI need the ARM64 builds of
  both before an ARM64 release.
- Executables are not signed (SmartScreen, macOS quarantine; see the README).
- CLI localization (`--lang`, Fluent) and `wol serve` are not implemented.
- JSON Schemas (`docs/schema/`) and fixtures (`docs/fixtures/`) do not exist yet; there is no
  GUI test project.

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

Launcher (run from `gui/launcher/`, needed by `dotnet publish`): `cargo build --release`.

A release keeps only the executables at the root of the publish folder: `wol-gui.exe`, a small
Rust launcher (`gui/launcher`) that starts `runtime\wol-gui.exe` with the same arguments, and
`wol.exe`. The GUI and the ~80 files of the Windows App SDK go to `runtime\`. The GUI finds its
root in `Services/AppFolder.cs`: `wol.exe`, the portable `wol.toml` / `wol-gui.json` and the
"Start with Windows" shortcut target all live at the root. A debug build keeps everything in
one folder.

The Native AOT link of `dotnet publish` runs `vswhere.exe`: put
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` on the `PATH` if it is not found.

The GUI build copies `cli/target/release/wol.exe` (or the debug build when there is no release
one) next to `wol-gui.exe`: build the CLI first, and rebuild it after changing it. A `wol.toml`
dropped in the GUI output folder is picked up in portable mode (ignored by Git); the GUI then
keeps its own settings in `wol-gui.json` next to it, otherwise in `%APPDATA%\wol\wol-gui.json`.

The `.ico` files in `gui/src/Wol.Desktop/Assets/` are generated from `design/assets/*.svg` by
`gui/tools/Build-Icons.ps1` (renders with Edge headless); run it again after changing the SVGs.

CI (`.github/workflows/ci.yaml`, on pushes to `master`, pull requests and published releases):
CLI lint, tests and release builds on Windows, Linux (musl) and macOS, x86_64 and ARM64;
launcher and GUI publish on Windows x64. Builds are kept as workflow artifacts. Follow a run
with `gh run watch <id>`.

## Releases

A version is published by creating a GitHub release from an annotated tag `vX.Y.Z` (procedure in
`docs/DEVELOPER.md`, section "Releases"). The release runs the CI on its tag; its `Release` job
checks that the tag matches the versions below, then attaches the archives
(`wol-vX.Y.Z-<system>-<arch>.zip / .tar.gz`, `wol-gui-vX.Y.Z-windows-x64.zip`).

The version lives in `cli/Cargo.toml`, `gui/launcher/Cargo.toml` (and both `Cargo.lock`), and
`<Version>` in `gui/src/Wol.Desktop/Wol.Desktop.csproj`. `MinimumCliVersion` in
`gui/src/Wol.Desktop/Cli/Contract.cs` is the oldest CLI the GUI accepts: raise it only when the
GUI starts using a newer CLI feature. The changelog entry goes on top of `docs/CHANGELOG.md`
(`## vX.Y.Z (YYYY-MM-DD)`, sections as `###` with their icons); the release notes are that entry
without its title.

## Testing the GUI

There is no GUI test project yet: check changes by hand.

- Run the GUI from a non-elevated console: WinUI 3 drag and drop (reordering groups) fails in
  an elevated process.
- Check AOT-sensitive changes (anything touching WinRT objects, windows, JSON) in the
  **published** build: some code works in a debug build and silently fails under Native AOT.
- The GUI uses the developer's own `%APPDATA%\wol\wol.toml` and `wol-gui.json` unless a
  `wol.toml` sits in the output folder: back them up before a test that changes them, or use a
  portable `wol.toml`.
- UI Automation (PowerShell `UIAutomationClient`) reads the window tree and state reliably;
  screenshots come out blank while the Windows session is locked.
- Only one instance runs (`Program.cs`): a second start brings the running window to the front.
  Stop `wol-gui` / `wol` before rebuilding, or the build cannot overwrite the executables.

## Architecture

### CLI layout

The crate is a library plus a thin binary (`src/main.rs` calls `cli::run`), so end-to-end
tests and future front-ends (`wol serve`) reuse the same code.

- `cli.rs`: clap definitions, command execution, human / JSON output. Each command returns an
  `Outcome` (JSON value, text, exit code); `run` prints it and adds the `schema` field.
  Help texts: doc comments, first paragraph for `-h`, further paragraphs for `--help`; explain
  what each option is for, keep every help line within 80 columns (`max_term_width`), and check
  with `wol <command> --help`.
- `config.rs`: `wol.toml` model and edits. Reads with `toml` (typed, validated model) and edits
  with `toml_edit` (`DocumentMut`) so hand-written comments survive. Every mutation edits the
  document, then `refresh()` re-parses and re-validates it: the file on disk can never become
  invalid through the CLI. Machine and group indexes in the model match the `[[machine]]` /
  `[[group]]` array indexes in the document.
- `error.rs`: one `Error` enum; each variant maps to a stable JSON `code`, `details` and an exit code.
- `mac.rs`, `magic.rs`: MAC address parsing, magic packet building and sending.
- `scan.rs`: local network scan (MAC and IPv4 address of every device that answers); it holds
  the platform-specific code (`SendARP` / `GetAdaptersAddresses` via `windows-sys`, netlink on
  Linux and `sysctl` on macOS via `libc`). `WOL_FAKE_SCAN` replaces the scan in tests.
  `probe.rs` turns a scan into machine states (online when any interface answers).
- `wake.rs`: magic packets to every interface, then for `--wait` scans until the machines are
  online, streaming text lines or NDJSON events.
- `watch.rs`: the `watch` loop (config reload on file change, debounced state tracking, NDJSON
  events). `time.rs`: RFC 3339 timestamps without a date library.

Platform-specific code cannot run here: after touching it, at least lint the other targets with
`cargo clippy --all-targets --target x86_64-unknown-linux-gnu` and `--target aarch64-apple-darwin`.

### The GUI drives the CLI

- The GUI ships `wol.exe` next to its own executable and runs it with `System.Diagnostics.Process`.
- One-shot commands (`list`, `status`, `scan`, `wake`, `add`, `edit`, `remove`, `group …`) are run with
  `--json`; continuous monitoring uses a single long-lived `wol watch --json` process whose
  `stdout` is NDJSON (one event per line).
- The GUI never reads or writes `wol.toml`: every change goes through CLI commands.
- A `wol serve` mode (same JSON over a named pipe or the network) is planned, so the GUI can
  later drive a CLI running on another machine. Keep the JSON shapes transport-agnostic.

### GUI layout

`gui/src/Wol.Desktop/`:

- `Program.cs` (entry point, single instance), `App.xaml`, `MainWindow` (navigation, window
  placement; `MainWindow.Tray.cs`: notification area, close to the tray, quit).
- `Cli/`: `CliClient` runs `wol.exe` and parses its JSON (source-generated `System.Text.Json`);
  `Contract.cs` holds the JSON schema and minimum CLI version the GUI expects.
- `ViewModels/MainViewModel*.cs`: machines, groups, states from `wol watch`, wake-ups and edits.
- `Models/`: `MachineItem`, `GroupSection`, `MachineState`, notifications.
- `Views/`: machine list (with group reordering), machine dialog, settings page, dialogs.
- `Controls/StateBadge`: the state badge (state colour, optional text such as "Never seen").
- `Services/`: `AppFolder` (release root), `AppSettings` (`wol-gui.json`, including the window
  placement), `TrayIcon` (`Shell_NotifyIcon`), `StartupShortcut`, `Strings` (resources).

WinUI 3 pitfalls met so far:

- Native AOT cannot cast `AppWindow.Presenter` to `OverlappedPresenter`: create one with
  `OverlappedPresenter.Create()`, `AppWindow.SetPresenter` it and keep the reference.
- Window placement: restored bounds are tracked through `AppWindow.Changed` and saved on close
  to the tray and on quit; saved bounds are clamped to the `OuterBounds` of their display (the
  `WorkArea` cuts snapped windows). No saved bounds means the first start: open maximized.
- Compact density drops the text padding of `AutoSuggestBox`: set it through
  `AutoSuggestBox.TextBoxStyle` based on `AutoSuggestBoxTextBoxStyle`.

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

Local network only: machines are found by their MAC addresses through an ARP scan of the local
subnets, without elevated privileges, which also gives their current IP address (see
`docs/cli-contract.md`, section "Local network scan").

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
  with `x:Uid` in XAML or `Strings.Get` / `Strings.Format` (`Services/Strings.cs`) in code. Layouts must tolerate ~30% longer French strings.
