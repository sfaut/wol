# Design brief: "Wol", a Wake-on-LAN machine manager for Windows

> Prompt sent to Claude Design to produce the GUI mockups. Keep it in sync with
> `docs/cli-contract.md` when the data model changes.

## Product

Wol lets a user keep a list of remote machines (PCs, servers, NAS), organised in groups,
see at a glance whether each one is online, and wake any offline machine (or a whole group)
with one click by sending a Wake-on-LAN magic packet.

The desktop app is a thin native front-end: all logic lives in a companion
command-line tool. The GUI displays states streamed by the CLI and triggers its
commands. Design for the GUI only.

## Platform and visual language

- Native Windows app built with **WinUI 3** (Windows App SDK), for Windows 10 (1809+) and Windows 11.
- It must look and feel like a first-party Windows 11 app (think Settings, Dev Home,
  PowerToys): Fluent Design, **Mica** window backdrop, Segoe UI Variable, **Segoe Fluent Icons**.
- Follow the system theme: light, dark and high contrast. Accent colour comes from Windows.
- Use **only standard WinUI 3 controls** and Windows Community Toolkit controls, so every
  screen maps directly to XAML. For each element, name the control you intend, for example
  `NavigationView`, `ItemsView` / `ListView`, `CommandBar`, `AutoSuggestBox`, `InfoBar`,
  `ContentDialog`, `TeachingTip`, `ProgressRing`, `ToggleSwitch`, `NumberBox`, `Expander`,
  `ComboBox`, `InfoBadge`, `SettingsCard` / `SettingsExpander`.
- Use no custom-drawn widgets, web-style components, illustrations baked with text,
  or brand colours outside the Windows accent.
- Fully usable with keyboard only and with a screen reader. Respect DPI scaling (100% to 200%).

## Localisation

- Ships in **English (default) and French**. Write all copy in English.
- Layouts must tolerate strings about **30% longer** (French). Avoid fixed-width text
  containers and avoid any text inside images.
- Dates, times and durations follow the user's locale ("2 min ago", "il y a 2 min").

## Data model (v1)

A **group** has a **name** (required, unique, e.g. "Office", "Home", "Servers").
Groups have a user-defined order.

A **machine** has:
- **Name** (required, unique, e.g. "Office PC")
- **Group** (optional; at most one; machines without a group appear under "Ungrouped")
- **Network interfaces** (1 to N), each with:
  - a **MAC address** (required; accept `AA:BB:CC:DD:EE:FF`, `AA-BB-…` or `AABBCC…`, display
    normalised). The same MAC address may be used by several machines (e.g. cloned virtual
    machines): allowed, but always flagged with a warning, since Wol cannot tell these machines
    apart when that address answers;
  - a **label** (optional, e.g. "Ethernet", "Wi-Fi").
- **Broadcast address** and **UDP port** for the magic packet (optional, advanced)

Wol works on the **local network only**. It finds machines by scanning the network for their MAC
addresses, which also gives their current IP address even when DHCP changes it. **Wake** sends the
magic packet to every interface of the machine. A machine is **online when at least one interface
answers**.

Live information shown per machine: **state**, **last seen**, and when online the interface that
answered and its current IP ("Online via Wi-Fi · 192.168.1.35"; no label: "Online · 192.168.1.35").

States, each needing a distinct, colour-blind-safe indicator (icon + label, colour is secondary):
- **Online**
- **Offline**
- **Waking**: packet sent, waiting for the machine to come up (show elapsed time; ends in Online or Timed out)
- **Timed out**: wake command sent but the machine never came up
- **Unknown**: not checked yet, or check in progress
- **Error**: the local network cannot be scanned

## Screens to design

1. **Navigation** (`NavigationView`)
   - Items: "All machines", then one item per group (with an `InfoBadge` counting offline
     machines), "Ungrouped" when relevant, and Settings at the bottom.
   - Group management from the pane: add, rename, reorder, delete
     (deleting a group asks whether to move its machines to "Ungrouped" or delete them).

2. **Main window: machine list**
   - List or cards (propose both, recommend one) showing state, name and last seen; the second line
     shows the current IP and the interface that answered when online, otherwise the first MAC
     address followed by "+N" when there are several (all interfaces in the tooltip).
   - Machines sharing a MAC address show a warning indicator, with the other machines named in its tooltip.
   - In "All machines", machines are shown in **collapsible sections per group**, each header
     showing the group name, its status summary ("2 online · 1 offline") and a **Wake group** action.
   - Primary action per row: **Wake**, prominent only when the machine is offline / timed out.
   - Secondary actions (context menu and overflow): Check now, Edit, Duplicate, Move to group,
     Copy MAC, Remove.
   - Toolbar: Add machine, Refresh all, search box (matches name, MAC addresses, labels and
     current IP), filter by state; multi-select to wake or move several machines.
   - Overall status summary (e.g. "3 online · 2 offline").
   - Drag and drop of machines between groups (with a keyboard-accessible equivalent: "Move to group").
   - **Empty states**: first launch (invite to add a first machine) and empty group.

3. **Add / Edit machine** (`ContentDialog` or dedicated page; recommend one)
   - Name and group first; group picked from an editable `ComboBox` that can also create a new group.
   - Then a **Network interfaces** list: one row per interface with the MAC address (`TextBox`),
     the label (editable `ComboBox` suggesting "Ethernet" and "Wi-Fi", free text allowed) and a
     remove button; an **Add interface** button below. At least one interface.
   - **Add from network**: a button listing the devices currently found on the network that no
     machine uses yet (MAC, current IP), to fill an interface without typing its MAC.
   - Advanced settings (broadcast address, UDP port) collapsed in an `Expander`.
   - Inline validation with clear messages: invalid MAC, MAC repeated within the machine and
     duplicate name block saving; a MAC already used by another machine is a warning that names
     that machine and does not block saving.

4. **Wake flow feedback**
   - Single machine: Sending → Waiting (progress + elapsed time, cancel) → Online (success)
     or Timed out (warning + Retry).
   - Group or multi-selection: aggregated progress ("3 of 5 online"), per-machine outcome,
     Retry for the ones that timed out.
   - Show how this appears both in the rows and as a transient notification inside the window.

5. **Settings** (Windows 11 Settings style, `SettingsCard` / `SettingsExpander`)
   - Appearance: theme (System / Light / Dark), language (System / English / Français).
   - Behaviour: start with Windows (off by default), close to notification area, start minimised.
   - Monitoring: check interval, wake timeout.
   - Notifications: notify when a machine goes online / offline (per state).
   - About: app version, CLI version and status, links.

6. **Notification area (system tray)**
   - Tray icon whose look reflects overall status (all online / some offline / error).
   - Right-click menu: Open Wol, a "Wake" submenu organised by group (each group offering
     "Wake all" plus its offline machines), Refresh, Quit.
   - Balloon notification examples ("Office PC is online", "NAS did not wake up",
     "Office: 4 of 5 machines online"). These are simple title + text balloons with no action buttons.

7. **Error and degraded states** (`InfoBar` at the top of the window)
   - Companion CLI missing or incompatible version.
   - Configuration file invalid (with the line in error if available).
   - No local network to scan.

## Window

- Default size about 1000 × 700, minimum about 500 × 500.
- Show the responsive behaviour: wide (navigation pane expanded), medium, compact
  (pane collapsed, list columns reduced).

## Keyboard shortcuts

Propose them and show them in tooltips, at least: Ctrl+N add machine, Ctrl+Shift+N add group,
F5 refresh, Ctrl+F search, Enter open / edit, Delete remove (with confirmation),
Ctrl+W wake selection, Ctrl+M move selection to group.

## Deliverables

1. Mockups of every screen and state above, in **light and dark** theme, at 100% scale.
2. For each screen, the list of WinUI 3 controls used and their layout (spacing in
   epx, following the Fluent 4 px grid).
3. State indicator set (icon glyph from Segoe Fluent Icons + label + theme colour token).
4. All UI copy in English, as a key / value list (e.g. `MachineList.Empty.Title`).
5. App icon proposal (simple, legible at 16 × 16 for the tray and up to 256 × 256).
6. Short interaction notes: focus order, animations (system Fluent transitions only), confirmations.

## Out of scope

macOS or Linux GUI, installers, accounts or cloud sync, telemetry.
