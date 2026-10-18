# Wol — hi-fi design

High-fidelity design for the Wol desktop app: a WinUI 3 (Windows App SDK) front-end for the Wol command-line tool, for Windows 10 1809+ and Windows 11. Every screen and state is drawn in light and dark theme at 100% scale (1 px = 1 epx), with the specs needed to build it in XAML.

Everything here is exported from the Claude Design canvas “Wol — WinUI 3 design” (private to its owner).

## What’s here

| Folder | Contents |
| --- | --- |
| [`mockups/`](mockups) | PNG exports — light theme on the left, dark theme on the right |
| [`html/`](html) | The same boards as standalone HTML; open them in a browser. [`wol-fluent.css`](html/wol-fluent.css) holds the theme variables, each mapped to its WinUI theme resource |
| [`specs/`](specs) | [Controls and layout](specs/layout.md) · [State indicators](specs/states.md) · [Interaction](specs/interaction.md) · [UI copy](specs/ui-copy.md) · [App icon](specs/app-icon.md) |
| [`assets/`](assets) | App icon and notification-area glyphs (SVG) |

## Decisions

- **Machine list:** ListView rows on two lines (recommended) rather than cards; cards (`ItemsView` + `UniformGridLayout`) are kept as the alternative. One `Expander` per group, with the group name, its status summary and **Wake group**.
- **Add / Edit machine:** `ContentDialog`, because the form is short and the list stays in view. Required fields come first, the group comes from an editable `ComboBox` that can also create a group, and advanced settings sit in a collapsed `Expander`.
- **Validation:** an invalid MAC or a duplicate name blocks saving; an unresolvable host is only a warning, since Wake-on-LAN needs the MAC and broadcast address, not the host.
- **Wake button:** accent when the machine is Offline or Timed out (labelled Retry after a timeout), standard when Unknown or Error, hidden when Online, replaced by Cancel while Waking.
- **Group InfoBadge:** counts Offline + Timed out machines, in the default (accent) style for contrast.
- **Feedback in the window:** a stack of InfoBars at the bottom of the content area; success closes after 5 s, warnings stay. Waking a group or a selection shows an InfoBar with a ProgressBar (“3 of 5 online”) and ends with Retry for the machines that timed out.
- **Degraded states:** one InfoBar at the top of the page, with precedence CLI missing → CLI incompatible → configuration invalid → network unavailable.
- **NavigationView:** `OpenPaneLength` 280, `CompactModeThresholdWidth` 641, `ExpandedModeThresholdWidth` 960, so the default 1000 epx window opens with the pane expanded.
- **Backdrop:** Mica on Windows 11, solid `SolidBackgroundFillColorBaseBrush` on Windows 10.
- **Shortcuts:** the brief’s set, plus Ctrl+R (check now), Ctrl+D (duplicate), Ctrl+Shift+C (copy MAC), F2 and Alt+Up / Alt+Down (groups), Ctrl+, (Settings) and F6 (move between areas).

## Screens

### Main window

![Machine list — list layout (recommended)](mockups/01-main-list.png)
![Machine list — card layout (alternative)](mockups/02-main-cards.png)
![Multi-select, context menu and row overflow](mockups/03-selection-and-menus.png)
![Search and state filter](mockups/04-search-and-filter.png)
![Drag and drop, and the keyboard Move to group](mockups/05-drag-and-drop.png)
![Empty state — first launch](mockups/06-empty-first-launch.png)
![Empty state — empty group and no results](mockups/07-empty-group-and-no-results.png)

### Groups and editing

![Group management from the pane](mockups/08-group-management.png)
![Add / Edit machine dialog](mockups/09-add-edit-machine.png)

### Wake flow

![Wake one machine](mockups/10-wake-one-machine.png)
![Wake a group or a selection](mockups/11-wake-group.png)

### Settings and notification area

![Settings](mockups/12-settings.png)
![Notification area — icon, menu, balloons](mockups/13-notification-area.png)

### Errors and window sizes

![Error and degraded states](mockups/14-errors.png)
![Responsive — wide, medium, compact](mockups/15-responsive.png)

## About the exports

- The PNGs were rendered in Chromium with Selawik, Microsoft’s open-source stand-in for Segoe UI. Open the HTML on Windows to see Segoe UI Variable.
- Icons in the mockups are drawn approximations of the Segoe Fluent Icons glyphs named in the specs.
- Values not known yet are left as placeholders, such as `[app version]` or `[path to the CLI]`.
