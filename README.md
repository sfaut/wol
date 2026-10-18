# Wol

Wake-on-LAN machine manager: keep a list of remote machines organised in groups,
monitor whether they are online, and wake them up with a magic packet.

> **Status: early development.** The features below describe the target of the first
> version; they are being implemented.

Wol comes in two parts:

- **`wol`**, a command-line tool (`cli/`, Rust) for Windows 10 / 11, Linux and macOS.
  Lightweight enough to run as a permanent monitor on a server or a Raspberry Pi.
- **`wol-gui`**, a native Windows 10 / 11 desktop app (`gui/`, C# / WinUI 3) built on top of the CLI.

## Features

### Machines and groups

- Describe each machine by a name, a MAC address and a host (IP address or hostname),
  with an optional broadcast address and TCP ports for status checks.
- Organise machines in ordered groups (Office, Home, Servers…).
- Everything is stored in a single, human-readable `wol.toml` file, editable by hand
  or through `wol` commands.

### Monitoring

- See at a glance which machines are online, offline, waking up or unreachable,
  with latency and last-seen time.
- Status checks use ICMP ping without administrator rights, with a TCP fallback for
  machines or networks that block ping.
- Continuous monitoring (`wol watch`) reports every state change as it happens.

### Wake-up

- Wake a machine, several machines or a whole group in one command or one click.
- Optionally wait until machines are actually online, with a timeout and a clear
  outcome for each machine.

### Command line

- Readable output for humans, and `--json` output with documented exit codes for
  scripts and integrations.
- Works the same on Windows, Linux (x86_64 and ARM, including Raspberry Pi) and macOS.

### Desktop app (Windows)

- Native Windows 11 look and feel: Fluent controls, Mica, light / dark / high-contrast
  themes following the system.
- Lives in the notification area: overall status at a glance, wake machines from the
  tray menu, notifications when a machine comes online or goes offline.
- Optional start with Windows.

### Everywhere

- Portable: no installer, no runtime to install, no registry entries. Copy the files
  where you want; put `wol.toml` next to the executable to carry everything on a USB drive.
- Available in English and French, following the system language.

## Build

```sh
# CLI
cd cli
cargo build --release              # -> cli/target/release/wol(.exe)

# Desktop app (Windows only)
cd gui
dotnet build
dotnet publish src/Wol.Desktop -c Release -r win-x64   # -> wol-gui.exe
```

## Documentation

- [`docs/cli-contract.md`](docs/cli-contract.md): configuration file, commands, JSON output.
- [`design/`](design): desktop app design (mockups, specs) and its [brief](design/brief.md).

## License

[MIT](LICENSE) © Sébastien Faut
