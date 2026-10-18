# Controls and layout

Every element maps to a WinUI 3 or Windows Community Toolkit control. Measurements are in epx on the 4 epx grid, at 100% scale, for the wide layout (1000 × 700, pane expanded) unless noted. Mockups: [`../mockups`](../mockups).

## Main window measurements

![Main window, list layout](../mockups/01-main-list.png)

| Element | Measurements |
| --- | --- |
| Title bar (`TitleBar`) | 48 high · pane toggle 32 at x 4 · app icon 16 at x 46 · caption buttons 46 wide |
| Navigation pane | `OpenPaneLength` 280 · items 36 high, margins 4 / 2 · icon 16 at x 16 · label at x 44 · selection pill 3 × 16 in `AccentFillColorDefaultBrush` |
| Content layer | `LayerFillColorDefaultBrush` · 1 epx `CardStrokeColorDefaultBrush` on top and left · top-left radius 8 |
| Page header | margins 36 left / 32 right · title 28/36 (`TitleTextBlockStyle`) 20 from the top · status summary 4 below, items 16 apart |
| Toolbar | `CommandBar` buttons 36 high, 8 above and 12 below · `AutoSuggestBox` 148–240 (takes the free width) · 8 · filter `DropDownButton` |
| Group section (`Expander`) | header ≥ 56, padding 16 / 8, radius 4 · 8 between sections |
| Machine row (`ListViewItem`) | 48 high · padding 16, 6, 8, 6 · columns 16 \| \* \| 172 \| 132, 12 apart |
| Notification host | `InfoBar` stack 16 from the bottom of the content area, 8 apart, at most 3 |
| Type ramp | Caption 12/16 · Body 14/20 · Body Strong 14/20 semibold · Subtitle 20/28 · Title 28/36 — Segoe UI Variable |
| Corner radii | controls 4 (`ControlCornerRadius`) · window, dialogs, flyouts, cards 8 (`OverlayCornerRadius`) |

## Window and navigation

| Element | Control | Properties and spacing |
| --- | --- | --- |
| Window | `Window + MicaBackdrop` | Mica (Base) on Windows 11; on Windows 10 fall back to SolidBackgroundFillColorBaseBrush. Default 1000 × 700, minimum 500 × 500 (OverlappedPresenter.PreferredMinimumWidth/Height), sizes multiplied by the monitor scale. |
| Title bar | `TitleBar` | ExtendsContentIntoTitleBar = true, height 48, IsPaneToggleButtonVisible = true, IsBackButtonVisible = false, icon 16, title “Wol” in CaptionTextBlockStyle. |
| Navigation | `NavigationView` | PaneDisplayMode Auto, OpenPaneLength 280, CompactModeThresholdWidth 641, ExpandedModeThresholdWidth 960 (so 1000 opens Expanded), IsSettingsVisible = true, IsPaneToggleButtonVisible = false (toggle is in the TitleBar). |
| All machines | `NavigationViewItem` | Icon FontIcon U+E71D AllApps. |
| Groups header | `NavigationViewItemHeader` | Text “Groups”; hidden when there are no groups. |
| Group item | `NavigationViewItem + InfoBadge` | Icon U+E8B7 Folder. InfoBadge Value = Offline + Timed out count, Visibility Collapsed at 0. ContextFlyout = MenuFlyout (Wake group, Rename…, Move up, Move down, Delete group…). CanDrag for reorder; AllowDrop for machines. |
| Ungrouped | `NavigationViewItem` | Shown only when at least one machine has no group. Always last, not reorderable. |
| New group | `NavigationViewItem` | Icon U+E710 Add, SelectsOnInvoked = false, opens the New group dialog (Ctrl+Shift+N). |

## Machine list page

| Element | Control | Properties and spacing |
| --- | --- | --- |
| Page grid | `Grid` | Rows: InfoBar slot · header · toolbar · list. Side margins 36 / 32 (Expanded), 24 / 24 (Compact pane), 16 / 16 (Minimal). |
| Title + summary | `TextBlock, ItemsRepeater` | TitleTextBlockStyle. Summary = horizontal StackLayout, Spacing 16, each item FontIcon 14 + Body text in TextFillColorSecondaryBrush. Wraps (ItemsRepeater + WrapLayout from the Toolkit). |
| Toolbar | `CommandBar` | DefaultLabelPosition Right, Background transparent, IsDynamicOverflowEnabled. AppBarButton Add machine (U+E710), Refresh all (U+E72C), AppBarSeparator, AppBarToggleButton Select (U+E762). |
| Search | `AutoSuggestBox` | Width 148–240: takes the space the CommandBar leaves and shrinks before anything wraps; collapses to an AppBarButton below 640. QueryIcon Find, filters live on TextChanged; matches name, MAC addresses, interface labels and current IP. |
| Filter | `DropDownButton + MenuFlyout` | ToggleMenuFlyoutItem per state with its state glyph as Icon and count as KeyboardAcceleratorTextOverride-style trailing text; “Show all states”. Label shows “All states” or “n states”. |
| Group section | `Expander` | One per group in an ItemsRepeater (StackLayout, Spacing 8). Header: name (BodyStrong) + summary (Caption, secondary) + Button “Wake group”. Content padding 0; IsExpanded persisted per group. |
| Machine rows | `ListView` | SelectionMode Extended (Multiple while Select is on). Item MinHeight 48, Padding 16, 6, 8, 6. Template Grid 16 \| \* \| 172 \| Auto, ColumnSpacing 12. CanDragItems = true. |
| Row second line | `TextBlock + ToolTip` | Online: label and current IP of the interface that answered, “Wi-Fi · 192.168.1.35” (no label: the IP alone). Otherwise the first MAC address, plus “+N” when there are more; the ToolTip lists every interface as “Label · MAC”. |
| Shared MAC warning | `FontIcon + ToolTip` | U+E7BA Warning, 16, SystemFillColorCautionBrush, 6 after the name, on every machine sharing a MAC. ToolTip names the other machines; the text is also part of the row automation name. |
| Row actions | `Button` | Wake: AccentButtonStyle when Offline or Timed out (label becomes Retry after a timeout), default style when Unknown, disabled in Error (no network to send on), collapsed when Online, replaced by Cancel while Waking. More: icon-only Button U+E712 with the row’s MenuFlyout. |
| Cards (alternative) | `ItemsView + UniformGridLayout` | MinItemWidth 196, MinItemHeight 148, spacing 8, card padding 16, radius 8. |
| Empty state | `StackPanel` | Centered, MaxWidth 420, Spacing 12: glyph 64, SubtitleTextBlockStyle, Body secondary, Buttons (Accent + default, Spacing 8), HyperlinkButton. |

## Add / Edit machine

| Element | Control | Properties and spacing |
| --- | --- | --- |
| Dialog | `ContentDialog` | Recommended over a page: the form stays short and keeps the list in view. Order: Name, Group, Network interfaces, Advanced. Title “Add machine” / “Edit machine”; PrimaryButtonText Add / Save; CloseButtonText Cancel; DefaultButton Primary; IsPrimaryButtonEnabled bound to form validity. Width 548. |
| Body | `ScrollViewer > StackPanel` | Spacing 16. Scrolls when Advanced is open or the window is short; buttons stay pinned. |
| Name | `TextBox` | First field. 1–64 characters, unique regardless of case. Header above (Body), Description below for help or the error (Caption). Validate on LostFocus and on submit; never in a separate dialog. |
| Group | `ComboBox` | IsEditable = true, Header “Group (optional)”. TextSubmitted with an unknown name stages a new group; Description says “New group … will be created”. |
| Network interfaces | `ItemsRepeater of Grid rows` | Section title (BodyStrong) + Caption description, then column headers “MAC address” and “Label (optional)”. One row per interface: Grid \* \| 148 \| 32, ColumnSpacing 8, RowSpacing 4 — TextBox MAC, editable ComboBox label (items Ethernet, Wi-Fi; free text), icon Button remove (U+E74D, disabled on the last row). Row messages span the three columns. |
| MAC address | `TextBox` | Accepts AA:BB:CC:DD:EE:FF, AA-BB-…, AABBCC…, any case; a message under the row previews the normalised value (“Will be saved as …”). |
| Add interface | `Button` | U+E710 + label, below the list, 8 before “Add from network…”. Adds an empty row and focuses its MAC. |
| Add from network… | `Button + Flyout` | Flyout 320 wide: title, Caption “Not used by any machine yet”, ListView (IsItemClickEnabled) of devices found on the local network — MAC (Body) over current IP (Caption) — then a ProgressRing “Still scanning…” footer, or “No new devices found”. Picking one fills the empty row (or adds a row) and closes the flyout. |
| Advanced | `Expander` | Collapsed by default. TextBox Broadcast address (PlaceholderText 255.255.255.255) and NumberBox UDP port (1–65535, PlaceholderText 9), each with a Caption explaining the default. |
| Validation | `TextBlock under the field or row` | Errors (block Save): invalid MAC, MAC repeated within the machine, duplicate name. Warning (does not block): MAC already used by another machine, naming it. Error = FontIcon 14 in SystemFillColorCriticalBrush + text; warning = caution glyph + primary text. LiveSetting Assertive. |

## Groups

| Element | Control | Properties and spacing |
| --- | --- | --- |
| New / Rename | `ContentDialog + TextBox` | Title “New group” / “Rename group”; Primary Create / Rename, disabled while the name is empty or already used; inline error under the TextBox. |
| Delete group | `ContentDialog + RadioButtons` | Title names the group. RadioButtons: “Move them to Ungrouped” (default), “Delete them too”. PrimaryButtonText “Delete group”, CloseButtonText Cancel, DefaultButton Close. |
| Reorder | `MenuFlyout + drag` | Move up / Move down (Alt+Up / Alt+Down) on the item’s ContextFlyout; drag in the pane as a shortcut. Order is saved immediately. |
| Move machines | `MenuFlyoutSubItem` | “Move to group” lists groups in order, then Ungrouped, separator, “New group…”. The current group shows a check and is disabled. |

## Wake feedback

| Element | Control | Properties and spacing |
| --- | --- | --- |
| Row while waking | `ProgressRing` | 16 × 16, IsIndeterminate; detail “Sending to 2 interfaces…”, then “m:ss · waiting for reply” updated every second. The packet goes to every interface of the machine. |
| Single result | `InfoBar` | In the notification host. Success: IsClosable, auto-close after 5 s (paused while hovered or focused). Warning (timed out): stays until closed, ActionButton Retry. |
| Group or selection | `InfoBar + ProgressBar` | IsClosable false while running, Severity Informational, Content = ProgressBar (Maximum = machines in the run, Value = online). ActionButton Cancel. Replaced by the result InfoBar with “Retry n machines”. |
| Notification host | `StackPanel` | Bottom-aligned in the content grid, margins 36 / 32 / 16, Spacing 8, at most 3 bars, ChildrenTransitions: EntranceThemeTransition + RepositionThemeTransition. |

## Settings

| Element | Control | Properties and spacing |
| --- | --- | --- |
| Page | `ScrollViewer > StackPanel` | MaxWidth 1000, margins as the list page, Spacing 4 between cards; section headers BodyStrongTextBlockStyle, Margin 1, 24, 0, 6. |
| Theme, Language | `SettingsCard + ComboBox` | Theme: Use system setting / Light / Dark (applies at once, sets RequestedTheme on the root). Language: Use system setting / English / Français (applies on restart; Description says so). ComboBox MinWidth 200. |
| Behavior | `SettingsCard + ToggleSwitch` | Start with Windows (opt-in shortcut in shell:startup, off by default — no registry writes), Start minimized (off), Close to the notification area (on). |
| Monitoring | `SettingsCard + NumberBox` | Check interval 1–3600 s (default 10, passed to wol watch --interval), Wake timeout 30–900 s (default 120). SpinButtonPlacementMode Inline, SmallChange 5, LargeChange 30, Width 132, unit TextBlock after. |
| Notifications | `SettingsExpander` | Header toggle = master switch. Items: SettingsCard with CheckBox per state (Comes online, Goes offline, Doesn’t wake up, Can’t be checked); items disabled when the master is off. |
| About | `SettingsExpander` | Header: app icon, “Wol”, version, Copy button. Items: Companion CLI (version, path, status glyph + label, Check again), Configuration file (path, Open file location), HyperlinkButtons. |

## Notification area

| Element | Control | Properties and spacing |
| --- | --- | --- |
| Icon | `Shell_NotifyIcon (Win32)` | Not a XAML control: hosted through a notify-icon helper. 16 × 16 monochrome .ico per status and taskbar theme; tooltip states the status in words (max 127 chars). |
| Menu | `MenuFlyout` | Open Wol (default item, bold), separator, MenuFlyoutSubItem Wake ▸ group ▸ “Wake all offline (n)” + offline machines, Refresh, separator, Quit. |
| Balloons | `NOTIFYICONDATA NIF_INFO` | Title + text only (NIIF_INFO, NIIF_WARNING for timed out). Title ≤ 63 chars, text ≤ 255. Respect the Notifications settings; skipped while the window is in the foreground — the in-window InfoBar covers it. |

## Errors and degraded states

| Element | Control | Properties and spacing |
| --- | --- | --- |
| Slot | `InfoBar` | Grid row 0 of every page, Margin 36, 12, 32, 0, IsOpen bound to app state, IsClosable false. One bar at a time; precedence: CLI missing → CLI incompatible → configuration invalid → no local network. |
| CLI missing | `InfoBar Error` | wol.exe is expected next to wol-gui.exe. ActionButton “Check again” + HyperlinkButton “How to install”. Checks and wake disabled; editing still allowed. |
| CLI incompatible | `InfoBar Error` | Shows required and found versions; ActionButton “Check again”, HyperlinkButton “How to update”. |
| Configuration invalid | `InfoBar Error` | Message includes line (and column) from the CLI. ActionButtons “Open file” and “Reload”. The last valid list stays visible, read-only. |
| No local network | `InfoBar Error` | The network can’t be scanned: every machine shows Error, Wake and Refresh are disabled. HyperlinkButton “Network settings” (ms-settings:network). Hides by itself when scanning works again, then a full refresh runs. |
