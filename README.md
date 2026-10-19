# Wol

Wake-on-LAN machine manager: keep a list of the machines of your local network, organised in
groups, see which ones are online, and wake them up with a magic packet.

> **Status: early development.** Version 0.1.0 is not released yet.

Wol comes in two parts, sharing one configuration file:

- **`wol`**, a command-line tool for Windows 10 / 11, Linux and macOS (x86_64 and ARM64).
  Light enough to run as a permanent monitor on a server or a Raspberry Pi.
- **`wol-gui`**, a native desktop app for Windows 10 (1809 or later) and Windows 11, built
  on top of `wol`.

Both are portable: no installer, nothing to install besides them, no registry entries.

## Contents

- [Before you start](#before-you-start)
- [Installation](#installation)
- [Desktop app (Windows)](#desktop-app-windows)
- [Command line](#command-line)
- [Configuration file](#configuration-file)
- [Troubleshooting](#troubleshooting)

## Before you start

Wake-on-LAN must be enabled on each machine to wake:

- in its firmware (BIOS / UEFI), often under "Power management" ("Wake on LAN", "Power on by
  PCI-E", etc.);
- in the settings of its network adapter (on Windows: Device Manager, adapter properties,
  "Power Management" and "Advanced" tabs, "Wake on Magic Packet").

Wol works on the local network: the computer running it must be on the same network as the
machines it monitors and wakes up.

## Installation

Download the archive for your system from the
[Releases page](https://github.com/sfaut/wol/releases):

| Archive | Content |
|---|---|
| `wol-gui-vX.Y.Z-windows-x64.zip` | Desktop app and command line, Windows x64 |
| `wol-vX.Y.Z-windows-x64.zip`, `wol-vX.Y.Z-windows-arm64.zip` | Command line, Windows |
| `wol-vX.Y.Z-linux-x64.tar.gz`, `wol-vX.Y.Z-linux-arm64.tar.gz` | Command line, Linux (static, any distribution, Raspberry Pi with a 64-bit system) |
| `wol-vX.Y.Z-macos-arm64.tar.gz`, `wol-vX.Y.Z-macos-x64.tar.gz` | Command line, macOS (Apple silicon, Intel) |

Extract it wherever you want, for example `C:\Tools\Wol` or `~/.local/bin`.

- **Windows:** the desktop app is `wol-gui.exe`. Keep the `runtime` folder next to it: it holds
  the rest of the app. The executables are not signed yet, so Windows SmartScreen may ask for a
  confirmation on the first start ("More info", then "Run anyway").
- **macOS:** the executable is not signed yet; allow it once with
  `xattr -d com.apple.quarantine wol`.
- To run `wol` from anywhere, add its folder to the `PATH`.

To update Wol, replace the files with those of the new version. To remove it, delete them, then
the configuration folder if you do not want to keep your machines (see
[Configuration file](#configuration-file)).

## Desktop app (Windows)

Start `wol-gui.exe`. The window lists your machines by group, with their state (online,
offline, waking up…), their current IP address and the network interface that answered.
States are checked continuously, every 10 seconds by default.

### Machines

- **Add a machine:** "Add" button. Give it a name and at least one MAC address; "Add from
  network" lists the devices found on the local network, to pick one without typing its address.
  A machine can have several network interfaces (Ethernet and Wi-Fi, for example), each with a
  label: Wol wakes them all and the machine is online as soon as one of them answers.
- **Wake a machine:** "Wake" button on its row. Wol then waits until it is online, and tells
  you when it does not answer in time ("Retry").
- **Other actions:** the "…" menu of a row: check now, edit, duplicate, move to a group,
  copy its MAC addresses, remove.
- **Search:** the search box filters the machines by name, MAC address, IP address or label.

### Groups

- Create a group with "New group" in the side bar; select a group to show only its machines.
- The menu of a group wakes all its machines, renames it, moves it up or down, or removes it
  (its machines can then be kept without a group). Groups can also be reordered by dragging
  them in the side bar.

### Notification area

Closing the window keeps Wol running in the notification area (it can be turned off in the
settings). Its icon shows the overall state; its menu opens the window, wakes all machines or a
group, refreshes the states, or quits Wol. Notifications tell you when a machine comes online,
does not wake up in time, or goes offline.

### Settings

The settings page (gear at the bottom of the side bar) sets:

- the theme (system, light, dark) and the language (system, English, French);
- starting with Windows (a shortcut in your Startup folder), starting in the notification
  area, closing to the notification area;
- the time between two checks and how long a wake-up waits for a machine;
- which notifications to show.

Its "About" section shows the versions, and opens the folder of the configuration file.

## Command line

```text
wol [OPTIONS] <COMMAND>
```

| Command | What it does |
|---|---|
| `wol list` | List groups and machines, with their network interfaces |
| `wol status [MACHINES]...` | Check whether machines are online (all by default) |
| `wol wake <MACHINES>...` | Wake machines up |
| `wol watch [MACHINES]...` | Monitor machines and print every state change |
| `wol scan` | List the devices found on the local network |
| `wol add <NAME> --mac <MAC>` | Add a machine |
| `wol edit <NAME> ...` | Change a machine |
| `wol remove <NAME>` | Remove a machine |
| `wol group add / rename / move / remove` | Manage groups |
| `wol version` | Print the version |

`wol <command> -h` gives a summary of a command, `wol <command> --help` all its details.
Machine and group names are case-insensitive; quote those with spaces (`"Office PC"`).

### Set up your machines

```sh
# Find the MAC address of a machine: it must be on.
wol scan
# 192.168.1.20     A4:BB:6D:12:34:56
# 192.168.1.30     00:11:32:BC:72:E9

# Groups are optional; they come in the order of creation.
wol group add Office
wol group add Servers

wol add "Office PC" --group Office --mac A4:BB:6D:12:34:56=Ethernet --mac A4:BB:6D:12:34:57=Wi-Fi
wol add NAS --group Servers --mac 00:11:32:BC:72:E9
```

`--mac` accepts `AA:BB:CC:DD:EE:FF`, `AA-BB-CC-DD-EE-FF`, `AABB.CCDD.EEFF` and `AABBCCDDEEFF`,
optionally followed by `=Label`. When the computer running Wol has several networks, give the
broadcast address of the right one with `--broadcast 192.168.1.255`; `--port` changes the UDP
port of the magic packet (9 by default).

Change a machine with `wol edit`; only the given options change:

```sh
wol edit NAS --name "NAS Origen"
wol edit "Office PC" --remove-mac A4:BB:6D:12:34:57
wol edit NAS --no-group
```

### Check and wake

```sh
wol status
# Office
#   Office PC  online   192.168.1.20 (Ethernet)
# Servers
#   NAS        offline

wol wake NAS                    # send the magic packets
wol wake --group Servers        # every machine of a group
wol wake NAS --wait             # then wait until it is online (120 s at most)
wol wake NAS --wait=300         # wait up to 300 s
```

`wol watch` prints the state of every machine, then one line per change until you stop it
(Ctrl+C); `--interval` sets the time between two checks (10 s by default). It takes changes of
the configuration file into account while it runs.

### Scripts

With `--json`, every command prints JSON for scripts and integrations, and `watch` prints one
JSON line per event. The exit code tells what happened:

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Generic failure (file access) |
| 2 | Invalid usage (bad arguments or values, name already used) |
| 3 | Invalid configuration file |
| 4 | Machine or group not found |
| 5 | Network error (no local network to scan) |
| 6 | Machines not online in time (`wake --wait`) |

The JSON format is described in [`docs/cli-contract.md`](docs/cli-contract.md).

## Configuration file

Machines and groups are kept in one text file, `wol.toml`, shared by the command line and the
desktop app. Wol uses the first of:

1. the file given with `--config <path>` (command line only);
2. `wol.toml` next to the `wol` executable (portable mode);
3. your configuration folder:
   - Windows: `%APPDATA%\wol\wol.toml`
   - Linux: `~/.config/wol/wol.toml`
   - macOS: `~/Library/Application Support/wol/wol.toml`

`wol` without a command shows which file it uses. The file is created on the first change.

**Portable mode:** put an empty `wol.toml` next to the executables, and everything stays in
that folder, for example on a USB drive. The desktop app then keeps its own settings there too,
in `wol-gui.json` (otherwise in `%APPDATA%\wol\`).

The file can also be edited by hand; your comments and layout are kept by Wol:

```toml
[[group]]
name = "Office"

[[group]]
name = "Servers"

[[machine]]
name = "Office PC"
group = "Office"
interfaces = [
    { mac = "A4:BB:6D:12:34:56", label = "Ethernet" },
    { mac = "A4:BB:6D:12:34:57", label = "Wi-Fi" },
]

[[machine]]
name = "NAS"
group = "Servers"
interfaces = [{ mac = "00:11:32:BC:72:E9" }]
broadcast = "192.168.1.255"   # optional, default 255.255.255.255
port = 9                      # optional, default 9
```

## Troubleshooting

- **A machine does not wake up.** Check that Wake-on-LAN is enabled in its firmware and network
  adapter (see [Before you start](#before-you-start)); some machines only wake from sleep, or
  need to have been shut down properly. Wi-Fi interfaces rarely support Wake-on-LAN: prefer
  Ethernet. When the computer running Wol has several networks, set `--broadcast`.
- **A machine shows offline but is on.** Wol finds machines by their MAC addresses on the local
  network: check the address with `wol scan`. A machine behind a router, or reached through a
  VPN, cannot be seen.
- **Two machines share a MAC address** (cloned virtual machines): Wol accepts it and warns
  you, but cannot tell them apart when that address answers.
- **The desktop app says `wol.exe` is missing.** Keep `wol.exe` next to `wol-gui.exe`, with
  the `runtime` folder.

## Contributing

To build Wol from its sources, see [`docs/DEVELOPER.md`](docs/DEVELOPER.md).

## License

[MIT](LICENSE) © Sébastien Faut
