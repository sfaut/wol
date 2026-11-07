# Changelog

## v1.1.0 (2026-11-07)

### 🚀 Launch

We are proud to announce Wol v1.1.0, which keeps chosen machines on: turn on "Wake
automatically" for a server or a NAS, and Wol wakes it whenever it finds it offline, then
tries again every minute until it answers.

### ✨ New features

- Wake automatically: a per-machine option (`auto_wake` in `wol.toml`) to keep a machine on.
  Whenever `wol watch` finds it offline, it sends its magic packets at once, then again at the
  first check a minute or more after the previous attempt, until the machine answers; a
  machine shut down on purpose is started again.
- Command line:
  - `wol add --auto-wake`, `wol edit --auto-wake` / `--no-auto-wake`; `wol list` tags these
    machines;
  - `wol watch` wakes them and reports each attempt (`auto_wake` event with `--json`; the
    `snapshot` event gives the retry delay in `auto_wake_retry_s`).
- Desktop app:
  - "Wake automatically" in Add / Edit, and a loop icon next to the name of these machines;
  - while Wol wakes one, its row shows "Waking automatically" with the time since the first
    packet; a notification tells when it starts, when the machine answers, and once if it is
    still offline after the wake-up timeout.

### 🐞 Bug fixes

- Desktop app: durations ("1 min 30 s", "5 min ago") no longer break across two lines.

### 🔒 Security

- The desktop app is built with the .NET 10.0.14 Native AOT compiler, which fixes the Brotli
  decoder vulnerability CVE-2026-111497
  ([GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9)). Wol never
  decompresses Brotli data, so v1.0.0 was not exposed.

### ⚙️ Internal improvements

- Dependencies: toml 1.1.8 → 1.2.1, toml_edit 0.25.17 → 0.25.19,
  Microsoft.WindowsAppSDK.WinUI 2.3.9 → 2.6.5,
  Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.9 → 2.6.5,
  Microsoft.Windows.SDK.BuildTools 10.0.28000.2705 → 10.0.28000.2957.
- CI: actions/checkout v5 → v7, actions/upload-artifact v4 → v7,
  actions/download-artifact v5 → v8, actions/setup-dotnet v5 → v6.

### 📝 Documentation

- README, command-line contract and design updated for Wake automatically.

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
