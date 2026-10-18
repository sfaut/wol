# UI copy — en-US

Every string shown in the mockups, keyed for `Strings/en-US/Resources.resw`. `{0}`, `{1}`… are runtime values; strings whose noun changes with the count have `.One` and `.Other`. Relative times and durations come from the locale formatter, so “il y a 2 min” needs no extra key. French (`fr-FR`) uses the same keys.

## Contents

- [App and navigation](#app-and-navigation)
- [Group menu (pane)](#group-menu-pane)
- [States](#states)
- [Status summary](#status-summary)
- [Toolbar, search and filter](#toolbar-search-and-filter)
- [Selection](#selection)
- [Group sections and rows](#group-sections-and-rows)
- [Machine menu](#machine-menu)
- [Drag and drop](#drag-and-drop)
- [Empty states](#empty-states)
- [Add / Edit machine](#add--edit-machine)
- [Validation](#validation)
- [Group dialogs](#group-dialogs)
- [Remove machines](#remove-machines)
- [Wake feedback (in window)](#wake-feedback-in-window)
- [Errors and degraded states](#errors-and-degraded-states)
- [Settings](#settings)
- [Notification area](#notification-area)
- [Tooltips with shortcuts](#tooltips-with-shortcuts)

## App and navigation

| Key | Value | Note |
| --- | --- | --- |
| `App.Name` | Wol |  |
| `Nav.AllMachines` | All machines |  |
| `Nav.GroupsHeader` | Groups |  |
| `Nav.Ungrouped` | Ungrouped |  |
| `Nav.NewGroup` | New group |  |
| `Nav.Settings` | Settings |  |
| `Nav.PaneToggle.Open` | Open navigation |  |
| `Nav.PaneToggle.Close` | Close navigation |  |
| `Nav.Group.BadgeName.One` | {0}, 1 machine not online | Automation name of a group item with its InfoBadge |
| `Nav.Group.BadgeName.Other` | {0}, {1} machines not online |  |

## Group menu (pane)

| Key | Value | Note |
| --- | --- | --- |
| `GroupMenu.WakeGroup` | Wake group |  |
| `GroupMenu.Rename` | Rename… |  |
| `GroupMenu.MoveUp` | Move up |  |
| `GroupMenu.MoveDown` | Move down |  |
| `GroupMenu.Delete` | Delete group… |  |

## States

| Key | Value | Note |
| --- | --- | --- |
| `State.Online` | Online |  |
| `State.Offline` | Offline |  |
| `State.Waking` | Waking |  |
| `State.TimedOut` | Timed out |  |
| `State.Unknown` | Unknown |  |
| `State.Error` | Error |  |
| `State.Online.Seen` | Seen just now | Cards only |
| `State.Offline.Detail` | Last seen {0} | Relative time from the formatter |
| `State.Offline.Never` | Never seen |  |
| `State.Waking.Sending.One` | Sending wake packet… |  |
| `State.Waking.Sending.Other` | Sending to {0} interfaces… | The packet goes to every interface |
| `State.Waking.Detail` | {0} · waiting for reply | Elapsed time, m:ss |
| `State.TimedOut.Detail` | No reply after {0} | Timeout, m:ss |
| `State.Unknown.NotChecked` | Not checked yet |  |
| `State.Unknown.Checking` | Checking… |  |
| `State.Unknown.CliUnavailable` | Not checked · CLI unavailable |  |
| `State.Error.NoNetwork` | Can’t scan the network |  |
| `Row.AutomationName` | {0}, {1}, {2} | Name, state, detail spelled out |

## Status summary

| Key | Value | Note |
| --- | --- | --- |
| `Summary.Online` | {0} online |  |
| `Summary.Offline` | {0} offline |  |
| `Summary.Waking` | {0} waking |  |
| `Summary.TimedOut` | {0} timed out |  |
| `Summary.Error.One` | {0} error |  |
| `Summary.Error.Other` | {0} errors |  |
| `Summary.Unknown` | {0} unknown |  |
| `Summary.NotOnline` | {0} not online | Compact group header |
| `Summary.Separator` |  ·  |  |
| `Summary.Unavailable.Other` | {0} machines · status unavailable |  |
| `Summary.NoNetwork.Other` | {0} machines · can’t scan the network |  |
| `Summary.Empty` | No machines |  |
| `Summary.AutomationName` | Status summary |  |

## Toolbar, search and filter

| Key | Value | Note |
| --- | --- | --- |
| `Page.AllMachines.Title` | All machines |  |
| `Toolbar.AddMachine` | Add machine |  |
| `Toolbar.RefreshAll` | Refresh all |  |
| `Toolbar.Refresh` | Refresh | On a group page |
| `Toolbar.Select` | Select |  |
| `Toolbar.Rename` | Rename | On a group page |
| `Toolbar.MoreGroupActions` | More group actions |  |
| `Search.Placeholder` | Search |  |
| `Search.Name` | Search by name, MAC address, label or IP | Automation name and tooltip |
| `Search.PlaceholderInGroup` | Search in {0} |  |
| `Search.Clear` | Clear search |  |
| `Search.Results` | {0} of {1} machines match “{2}” |  |
| `Search.ClearAll` | Clear search and filter |  |
| `Filter.Name` | Filter by state |  |
| `Filter.AllStates` | All states |  |
| `Filter.SomeStates` | {0} states |  |
| `Filter.ShowAll` | Show all states |  |

## Selection

| Key | Value | Note |
| --- | --- | --- |
| `Selection.Count` | {0} selected |  |
| `Selection.Wake` | Wake |  |
| `Selection.MoveToGroup` | Move to group |  |
| `Selection.Remove` | Remove |  |
| `Selection.Clear` | Clear selection |  |
| `Selection.Checkbox.Name` | Select {0} |  |

## Group sections and rows

| Key | Value | Note |
| --- | --- | --- |
| `Section.WakeGroup` | Wake group |  |
| `Section.Collapse` | Collapse {0} |  |
| `Section.Expand` | Expand {0} |  |
| `Section.Shown` | {0} of {1} machines shown | While searching or filtering |
| `Row.Wake` | Wake |  |
| `Row.Retry` | Retry |  |
| `Row.Cancel` | Cancel |  |
| `Row.Wake.Name` | Wake {0} |  |
| `Row.Retry.Name` | Retry waking {0} |  |
| `Row.Cancel.Name` | Cancel waking {0} |  |
| `Row.More.Name` | More actions for {0} |  |
| `Row.Online.Line` | {0} · {1} | Label · current IP of the interface that answered |
| `Row.Online.LineNoLabel` | {0} | Current IP when that interface has no label |
| `Row.Mac.More` | {0} +{1} | First MAC address + number of other interfaces |
| `Row.Interfaces.Tooltip.Line` | {0} · {1} | One line per interface: label · MAC (MAC alone without a label) |
| `Row.SharedMac.Tooltip.One` | Same MAC address as {0}. Wol can’t tell these machines apart when this address answers. |  |
| `Row.SharedMac.Tooltip.Other` | Same MAC address as {0} and {1} other machines. Wol can’t tell these machines apart when this address answers. |  |
| `Row.SharedMac.Name` | Shares its MAC address with {0} | Appended to the row automation name |
| `Row.AlreadyOnline` | Already online | Group wake detail |
| `Row.WokeIn` | Woke in {0} | Group wake detail |

## Machine menu

| Key | Value | Note |
| --- | --- | --- |
| `RowMenu.Wake` | Wake |  |
| `RowMenu.WakeMany` | Wake {0} machines |  |
| `RowMenu.CheckNow` | Check now |  |
| `RowMenu.Edit` | Edit |  |
| `RowMenu.Duplicate` | Duplicate |  |
| `RowMenu.MoveToGroup` | Move to group |  |
| `RowMenu.MoveToGroup.Current` | Current |  |
| `RowMenu.MoveToGroup.NewGroup` | New group… |  |
| `RowMenu.CopyMac` | Copy MAC address | Machine with one interface |
| `RowMenu.CopyMacs` | Copy MAC addresses | Submenu when there are several interfaces |
| `RowMenu.CopyMac.Item` | {0} · {1} | Label · MAC |
| `RowMenu.CopyMac.All` | All, one per line |  |
| `RowMenu.Remove` | Remove |  |
| `RowMenu.RemoveMany` | Remove {0} machines |  |
| `Duplicate.Name` | {0} (copy) |  |
| `CopyMac.Announcement` | MAC address copied |  |

## Drag and drop

| Key | Value | Note |
| --- | --- | --- |
| `Drag.MoveTo` | Move to {0} |  |
| `Drag.Moved.Announcement.One` | Moved {0} to {1} |  |
| `Drag.Moved.Announcement.Other` | Moved {0} machines to {1} |  |

## Empty states

| Key | Value | Note |
| --- | --- | --- |
| `Empty.First.Title` | Add your first machine |  |
| `Empty.First.Body` | Wol wakes computers on your local network by sending them a Wake-on-LAN packet. Add a machine by its MAC address, or pick it from the devices Wol finds on your network. |  |
| `Empty.First.Add` | Add machine |  |
| `Empty.First.CreateGroup` | Create a group |  |
| `Empty.First.Help` | How to turn on Wake-on-LAN on a PC |  |
| `Empty.Group.Title` | {0} has no machines yet |  |
| `Empty.Group.Body` | Add a machine to this group, or drag machines here from another group. You can also use Move to group (Ctrl+M) on any machine. |  |
| `Empty.Group.GoToAll` | Go to All machines |  |
| `Empty.Search.Title` | No machines match “{0}” |  |
| `Empty.Search.Body` | Check the spelling, or search by name, MAC address, label or IP address. A state filter may also be hiding machines. |  |
| `Empty.Search.Clear` | Clear search |  |

## Add / Edit machine

| Key | Value | Note |
| --- | --- | --- |
| `MachineDialog.AddTitle` | Add machine |  |
| `MachineDialog.EditTitle` | Edit machine |  |
| `MachineDialog.Add` | Add |  |
| `MachineDialog.Save` | Save |  |
| `MachineDialog.Cancel` | Cancel |  |
| `MachineDialog.Name.Header` | Name |  |
| `MachineDialog.Group.Header` | Group (optional) |  |
| `MachineDialog.Group.Placeholder` | No group |  |
| `MachineDialog.Group.ShowList` | Show groups |  |
| `MachineDialog.Group.New` | New group “{0}” will be created |  |
| `MachineDialog.Interfaces.Header` | Network interfaces |  |
| `MachineDialog.Interfaces.Description` | Wol wakes every interface and shows the machine online as soon as one of them answers. |  |
| `MachineDialog.Interface.Mac.Header` | MAC address |  |
| `MachineDialog.Interface.Mac.Placeholder` | AA:BB:CC:DD:EE:FF |  |
| `MachineDialog.Interface.Mac.Name` | MAC address, interface {0} |  |
| `MachineDialog.Interface.Mac.Preview` | Will be saved as {0} |  |
| `MachineDialog.Interface.Label.Header` | Label (optional) |  |
| `MachineDialog.Interface.Label.Placeholder` | Label |  |
| `MachineDialog.Interface.Label.Name` | Label, interface {0} |  |
| `MachineDialog.Interface.Label.Ethernet` | Ethernet | Suggested label |
| `MachineDialog.Interface.Label.WiFi` | Wi-Fi | Suggested label |
| `MachineDialog.Interface.Label.ShowList` | Show labels |  |
| `MachineDialog.Interface.Remove.Name` | Remove interface {0} |  |
| `MachineDialog.Interface.Remove.LastName` | Remove interface {0} (a machine needs at least one) |  |
| `MachineDialog.AddInterface` | Add interface |  |
| `MachineDialog.AddFromNetwork` | Add from network… |  |
| `Discovery.Title` | Devices on your network |  |
| `Discovery.Subtitle` | Not used by any machine yet · pick one to add it |  |
| `Discovery.Scanning` | Still scanning… |  |
| `Discovery.Empty` | No new devices found |  |
| `Discovery.Item.Name` | {0}, {1} | MAC address, current IP |
| `MachineDialog.Advanced.Header` | Advanced |  |
| `MachineDialog.Advanced.Description` | Broadcast address, UDP port |  |
| `MachineDialog.Broadcast.Header` | Broadcast address |  |
| `MachineDialog.Broadcast.Placeholder` | 255.255.255.255 |  |
| `MachineDialog.Broadcast.Help` | Leave empty to send the packet to the whole local network |  |
| `MachineDialog.Port.Header` | UDP port |  |
| `MachineDialog.Port.Placeholder` | 9 |  |
| `MachineDialog.Port.Help` | Port of the magic packet (1–65535). Leave empty for 9. |  |

## Validation

| Key | Value | Note |
| --- | --- | --- |
| `Validation.Name.Required` | Enter a name. |  |
| `Validation.Name.TooLong` | Use 64 characters or fewer. |  |
| `Validation.Name.Duplicate` | A machine named “{0}” already exists. Names don’t depend on capital letters. |  |
| `Validation.Mac.Required` | Enter the MAC address, or remove this interface. |  |
| `Validation.Mac.Invalid` | A MAC address has 12 hexadecimal digits, for example 3C:52:82:1A:4F:07. |  |
| `Validation.Mac.Repeated` | This MAC address is already in the list. |  |
| `Validation.Mac.Shared` | {0} uses this MAC address too. Wol can’t tell the two machines apart when it answers. You can still save. | Warning, does not block saving |
| `Validation.Broadcast.Invalid` | Enter an IPv4 broadcast address, for example 192.168.1.255. |  |
| `Validation.Port.Invalid` | Use a port from 1 to 65535. |  |
| `Validation.Group.Required` | Enter a group name. |  |
| `Validation.Group.Duplicate` | A group named “{0}” already exists. |  |
| `Validation.Group.Reserved` | “Ungrouped” is reserved. Choose another name. |  |

## Group dialogs

| Key | Value | Note |
| --- | --- | --- |
| `GroupDialog.NewTitle` | New group |  |
| `GroupDialog.RenameTitle` | Rename group |  |
| `GroupDialog.Name.Header` | Group name |  |
| `GroupDialog.Create` | Create |  |
| `GroupDialog.Rename` | Rename |  |
| `GroupDialog.Cancel` | Cancel |  |
| `DeleteGroup.Title` | Delete the group “{0}”? |  |
| `DeleteGroup.Body.One` | {0} contains 1 machine. What should happen to it? |  |
| `DeleteGroup.Body.Other` | {0} contains {1} machines. What should happen to them? |  |
| `DeleteGroup.Body.Empty` | This group has no machines. |  |
| `DeleteGroup.Option.Move` | Move them to Ungrouped |  |
| `DeleteGroup.Option.Delete` | Delete them too |  |
| `DeleteGroup.Confirm` | Delete group |  |
| `DeleteGroup.Cancel` | Cancel |  |

## Remove machines

| Key | Value | Note |
| --- | --- | --- |
| `RemoveDialog.Title.One` | Remove {0}? |  |
| `RemoveDialog.Title.Other` | Remove {0} machines? |  |
| `RemoveDialog.Body.One` | It is removed from Wol only; nothing changes on the machine. |  |
| `RemoveDialog.Body.Other` | They are removed from Wol only; nothing changes on the machines. |  |
| `RemoveDialog.Confirm` | Remove |  |
| `RemoveDialog.Cancel` | Cancel |  |

## Wake feedback (in window)

| Key | Value | Note |
| --- | --- | --- |
| `Wake.Success.Title` | {0} is online |  |
| `Wake.Success.Message` | It woke up in {0}. |  |
| `Wake.TimedOut.Title` | {0} did not wake up |  |
| `Wake.TimedOut.Message` | No reply after {0}. Check that Wake-on-LAN is turned on for this PC. |  |
| `Wake.Retry` | Retry |  |
| `Wake.SendFailed.Title` | Couldn’t send the wake packet |  |
| `Wake.SendFailed.Message` | {0} | Message from the CLI |
| `Wake.Progress.Title.Group` | Waking {0} |  |
| `Wake.Progress.Title.Selection` | Waking {0} machines |  |
| `Wake.Progress.Message` | {0} of {1} online · {2} | {2} = state counts and elapsed time |
| `Wake.Progress.Name` | Machines online |  |
| `Wake.Progress.Cancel` | Cancel |  |
| `Wake.Result.AllOnline.Title` | {0}: all {1} machines online |  |
| `Wake.Result.AllOnline.Message` | Woken in {0}. |  |
| `Wake.Result.Partial.Title` | {0}: {1} of {2} machines online |  |
| `Wake.Result.Partial.Message.One` | {0} did not wake up. | Machine name |
| `Wake.Result.Partial.Message.Other` | {0} machines did not wake up. |  |
| `Wake.Result.Retry.One` | Retry 1 machine |  |
| `Wake.Result.Retry.Other` | Retry {0} machines |  |
| `Notifications.MoreCount` | +{0} more |  |
| `Notifications.Close` | Close |  |

## Errors and degraded states

| Key | Value | Note |
| --- | --- | --- |
| `Error.CliMissing.Title` | Companion CLI not found |  |
| `Error.CliMissing.Message` | Wol needs wol.exe, its command-line tool, in the same folder as wol-gui.exe ({0}). | App folder |
| `Error.CliMissing.CheckAgain` | Check again |  |
| `Error.CliMissing.Help` | How to install |  |
| `Error.CliIncompatible.Title` | Companion CLI version not supported |  |
| `Error.CliIncompatible.Message` | This version of Wol needs wol.exe {0} or later. Found {1}. |  |
| `Error.CliIncompatible.CheckAgain` | Check again |  |
| `Error.CliIncompatible.Help` | How to update |  |
| `Error.Config.Title` | Configuration file can’t be read |  |
| `Error.Config.MessageWithLine` | Line {0}: {1} Your machines are shown as last saved until the file is fixed. | {1} = CLI message |
| `Error.Config.Message` | {0} Your machines are shown as last saved until the file is fixed. |  |
| `Error.Config.Open` | Open file |  |
| `Error.Config.Reload` | Reload |  |
| `Error.NoNetwork.Title` | No local network to scan |  |
| `Error.NoNetwork.Message` | Connect this PC to a local network (Ethernet or Wi-Fi) to find and wake your machines. Wol tries again automatically. |  |
| `Error.NoNetwork.Settings` | Network settings | Opens ms-settings:network |

## Settings

| Key | Value | Note |
| --- | --- | --- |
| `Settings.Title` | Settings |  |
| `Settings.Appearance` | Appearance |  |
| `Settings.Theme.Header` | App theme |  |
| `Settings.Theme.Description` | Light, dark or the same as Windows |  |
| `Settings.Theme.System` | Use system setting |  |
| `Settings.Theme.Light` | Light |  |
| `Settings.Theme.Dark` | Dark |  |
| `Settings.Language.Header` | Language |  |
| `Settings.Language.Description` | Changes apply the next time Wol starts |  |
| `Settings.Language.System` | Use system setting |  |
| `Settings.Language.English` | English | Never translated |
| `Settings.Language.French` | Français | Never translated |
| `Settings.Behavior` | Behavior |  |
| `Settings.StartWithWindows.Header` | Start with Windows |  |
| `Settings.StartWithWindows.Description` | Adds a shortcut to your Startup folder so Wol runs in the notification area when you sign in |  |
| `Settings.StartMinimized.Header` | Start minimized |  |
| `Settings.StartMinimized.Description` | Open to the notification area instead of showing the window |  |
| `Settings.CloseToTray.Header` | Close to the notification area |  |
| `Settings.CloseToTray.Description` | Closing the window keeps Wol running so it can check machines and notify you. Quit from the notification area icon. |  |
| `Settings.Monitoring` | Monitoring |  |
| `Settings.CheckInterval.Header` | Check interval |  |
| `Settings.CheckInterval.Description` | How often Wol checks whether machines are online (1–3600 seconds) |  |
| `Settings.WakeTimeout.Header` | Wake timeout |  |
| `Settings.WakeTimeout.Description` | Stop waiting and mark the machine Timed out after this long (30–900 seconds) |  |
| `Settings.Unit.Seconds` | seconds |  |
| `Settings.NumberBox.Increase` | Increase |  |
| `Settings.NumberBox.Decrease` | Decrease |  |
| `Settings.Notifications` | Notifications |  |
| `Settings.Notify.Header` | Machine notifications |  |
| `Settings.Notify.Description` | Windows notifications from the notification area icon |  |
| `Settings.Notify.Intro` | Notify me when a machine… |  |
| `Settings.Notify.Online` | Comes online |  |
| `Settings.Notify.Offline` | Goes offline |  |
| `Settings.Notify.TimedOut` | Doesn’t wake up (timed out) |  |
| `Settings.Notify.Error` | Can’t be checked (no local network to scan) |  |
| `Settings.About` | About |  |
| `Settings.About.Version` | Version {0} |  |
| `Settings.About.Copy` | Copy |  |
| `Settings.About.Cli.Header` | Companion CLI |  |
| `Settings.About.Cli.Description` | wol.exe {0}, next to wol-gui.exe | CLI version |
| `Settings.About.Cli.Connected` | Connected |  |
| `Settings.About.Cli.NotFound` | Not found |  |
| `Settings.About.Cli.Incompatible` | Not supported |  |
| `Settings.About.Cli.CheckAgain` | Check again |  |
| `Settings.About.Config.Header` | Configuration file |  |
| `Settings.About.Config.OpenLocation` | Open file location |  |
| `Settings.About.Docs` | Documentation |  |
| `Settings.About.Report` | Report a problem |  |
| `Settings.About.Notices` | Third-party notices |  |
| `Toggle.On` | On |  |
| `Toggle.Off` | Off |  |

## Notification area

| Key | Value | Note |
| --- | --- | --- |
| `Tray.Tooltip.AllOnline` | Wol — all {0} machines online |  |
| `Tray.Tooltip.Mixed` | Wol — {0} online · {1} not online |  |
| `Tray.Tooltip.Error` | Wol — {0} | Title of the current error |
| `Tray.Menu.Open` | Open Wol |  |
| `Tray.Menu.Wake` | Wake |  |
| `Tray.Menu.WakeAll` | Wake all offline ({0}) |  |
| `Tray.Menu.GroupAllOnline` | {0} — all online |  |
| `Tray.Menu.MachineWaking` | {0} — waking… |  |
| `Tray.Menu.Refresh` | Refresh |  |
| `Tray.Menu.Quit` | Quit |  |
| `Balloon.Online.Title` | {0} is online |  |
| `Balloon.Online.Woken` | It woke up in {0}. |  |
| `Balloon.Online.Passive` | It just came online. |  |
| `Balloon.Offline.Title` | {0} went offline |  |
| `Balloon.Offline.Text` | Last reply {0}. |  |
| `Balloon.TimedOut.Title` | {0} did not wake up |  |
| `Balloon.TimedOut.Text` | No reply after {0}. Check that Wake-on-LAN is turned on. |  |
| `Balloon.Error.Title` | Can’t check your machines |  |
| `Balloon.Error.Text` | No local network to scan. |  |
| `Balloon.Group.Title` | {0}: {1} of {2} machines online |  |
| `Balloon.Group.Text.One` | {0} did not wake up. |  |
| `Balloon.Group.Text.Other` | {0} machines did not wake up. |  |
| `Balloon.StillRunning.Title` | Wol is still running |  |
| `Balloon.StillRunning.Text` | Find it in the notification area. You can change this in Settings. |  |

## Tooltips with shortcuts

| Key | Value | Note |
| --- | --- | --- |
| `Tooltip.AddMachine` | Add machine (Ctrl+N) |  |
| `Tooltip.NewGroup` | New group (Ctrl+Shift+N) |  |
| `Tooltip.RefreshAll` | Refresh all (F5) |  |
| `Tooltip.Search` | Search (Ctrl+F) |  |
| `Tooltip.Wake` | Wake (Ctrl+W) |  |
| `Tooltip.WakeAgain` | Wake again (Ctrl+W) |  |
| `Tooltip.Cancel` | Stop waiting (Esc) |  |
| `Tooltip.MoveToGroup` | Move to group (Ctrl+M) |  |
| `Tooltip.Remove` | Remove (Delete) |  |
| `Tooltip.ClearSelection` | Clear selection (Esc) |  |
| `Tooltip.RenameGroup` | Rename group (F2) |  |
| `Tooltip.Settings` | Settings (Ctrl+,) |  |
