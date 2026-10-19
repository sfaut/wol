# Changelog

## v1.0.0 (2026-10-19)

### 🚀 Launch

We are proud to announce Wol v1.0.0, the first release of Wol: a Wake-on-LAN machine manager
to keep a list of the machines of your local network, organised in groups, see which ones are
online, and wake them up.

- `wol`, a command-line tool for Windows, Linux and macOS, x86_64 and ARM64.
- `wol-gui`, a native desktop app for Windows 10 / 11, built on top of `wol`.
- Portable: no installer, no runtime to install, no registry entries.

### ✨ New features

- Machines described by one or more network interfaces (MAC address and optional label), with
  an optional broadcast address and port, organised in ordered groups.
- One human-readable `wol.toml` file shared by the command line and the desktop app, editable by
  hand (comments and layout are kept), with a portable mode: `wol.toml` next to the executables.
- State checks by scanning the local network for the MAC addresses of the machines, without
  administrator rights, which also gives their current IP address and the interface that answered.
- Command line:
  - `list`, `add`, `edit`, `remove` and `group add / rename / move / remove` to manage machines
    and groups;
  - `status` to check machines once, `watch` to follow every state change;
  - `scan` to list the devices of the local network and find a MAC address;
  - `wake` to wake machines or whole groups, with `--wait` to wait until they are online;
  - `--json` output and documented exit codes for scripts and integrations.
- Desktop app:
  - machines listed by group with live state badges, IP addresses and search;
  - wake a machine or a group in one click, with the outcome of each wake-up;
  - add and edit machines, picking a device found on the network; duplicate, move and remove
    machines; create, rename, reorder (drag and drop) and remove groups;
  - notification area icon with the overall state, a menu to wake machines, and notifications
    when machines come online, go offline or do not wake up;
  - settings: theme, language, start with Windows, start in the notification area, close to
    the notification area, check interval, wake-up timeout, notifications;
  - Windows 11 look (Fluent, Mica, light / dark themes), in English and French;
  - the window opens maximized the first time, then where it was left.

### 📝 Documentation

- README for end users: installation, desktop app, command line, configuration file and
  troubleshooting.
- Developer guide (`docs/DEVELOPER.md`) and the contract of the command line
  (`docs/cli-contract.md`).
