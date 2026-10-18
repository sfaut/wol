using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wol.Desktop.Models;
using Wol.Desktop.Services;
using Wol.Desktop.ViewModels;
using Wol.Desktop.Views;
using Windows.Graphics;
using Windows.System;

namespace Wol.Desktop;

/// <summary>
/// Main application window: navigation by group and the machine list.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Default window size, in epx (scaled by the monitor DPI).
    /// </summary>
    private static readonly SizeInt32 DefaultSize = new(1000, 700);

    /// <summary>
    /// Smallest window size, in epx (scaled by the monitor DPI).
    /// </summary>
    private static readonly SizeInt32 MinimumSize = new(500, 500);

    /// <summary>
    /// "All machines" navigation item.
    /// </summary>
    private readonly NavigationViewItem _allMachines;

    /// <summary>
    /// "New group" navigation item, which opens a dialog instead of a page.
    /// </summary>
    private readonly NavigationViewItem _newGroup;

    /// <summary>
    /// App state, fed by the CLI.
    /// </summary>
    private readonly MainViewModel _viewModel;

    /// <summary>
    /// Whether the window is the active one (notifications are then shown in it, not as balloons).
    /// </summary>
    private bool _isActive;

    /// <summary>
    /// Group to select at the next navigation rebuild (after a rename or a creation).
    /// </summary>
    private string? _selectGroup;

    /// <summary>
    /// Reorders groups by drag and drop, in the navigation pane and in the machine list.
    /// </summary>
    private readonly GroupReorder _reorder;

    /// <summary>
    /// Position and size of the window when restored (not maximized nor minimized).
    /// </summary>
    private WindowBounds _restoredBounds = new(0, 0, 0, 0);

    /// <summary>
    /// Whether the window opens maximized the next time it is shown (first use, or maximized
    /// when last closed).
    /// </summary>
    private bool _maximizeOnShow;

    /// <summary>
    /// Presenter of the window, kept rather than read back from <c>AppWindow.Presenter</c>:
    /// under Native AOT, that one cannot be cast to <see cref="OverlappedPresenter"/>.
    /// </summary>
    private readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();

    /// <summary>
    /// Builds the window, its navigation, and starts loading the machines.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // Window is not a FrameworkElement: x:Uid does not apply, the title is localized in code.
        Title = Strings.Get("AppDisplayName");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyPlacement();
        if (!MicaController.IsSupported())
        {
            // Windows 10: no Mica, the solid base background instead.
            Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }

        _allMachines = new NavigationViewItem
        {
            Content = Strings.Get("Nav_AllMachines"),
            Icon = new FontIcon { Glyph = "\uE71D" },
        };
        _newGroup = new NavigationViewItem
        {
            Content = Strings.Get("Nav_NewGroup"),
            Icon = new FontIcon { Glyph = "\uE710" },
            SelectsOnInvoked = false,
        };
        ToolTipService.SetToolTip(_newGroup, Strings.Get("Nav_NewGroup_Tooltip"));

        var newGroupShortcut = new KeyboardAccelerator { Key = VirtualKey.N, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        newGroupShortcut.Invoked += (accelerator, args) =>
        {
            args.Handled = true;
            _ = NewGroupAsync();
        };
        Root.KeyboardAccelerators.Add(newGroupShortcut);

        var settingsShortcut = new KeyboardAccelerator { Key = (VirtualKey)0xBC, Modifiers = VirtualKeyModifiers.Control };
        settingsShortcut.Invoked += (accelerator, args) =>
        {
            args.Handled = true;
            Navigation.SelectedItem = Navigation.SettingsItem;
        };
        Root.KeyboardAccelerators.Add(settingsShortcut);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "wol-gui.ico"));
        ApplyTheme();

        _viewModel = new MainViewModel(DispatcherQueue)
        {
            CheckInterval = AppSettings.Current.CheckInterval,
            WakeTimeout = TimeSpan.FromSeconds(AppSettings.Current.WakeTimeout),
        };
        _viewModel.GroupsChanged += (_, _) => BuildNavigation();
        _viewModel.StatesChanged += (_, _) => UpdateBadges();
        _reorder = new GroupReorder(_viewModel, DropLayer, DropMarker);
        ListPage.Reorder = _reorder;
        ListPage.ViewModel = _viewModel;
        ListPage.ShowAllRequested += (_, _) => Navigation.SelectedItem = _allMachines;
        SettingsView.ViewModel = _viewModel;
        SettingsView.ThemeChanged += (_, _) => ApplyTheme();
        BuildNavigation();

        StartTray();
        Activated += (_, args) => _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        AppWindow.Closing += AppWindow_Closing;

        // Monitoring runs whether the window is shown or not (start in the notification area).
        _ = _viewModel.StartAsync();
    }

    /// <summary>
    /// Applies the theme of the settings to the window content (dialogs and menus follow).
    /// </summary>
    private void ApplyTheme()
    {
        Root.RequestedTheme = AppSettings.Current.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // The caption buttons are drawn by the system: they follow the theme separately.
        AppWindow.TitleBar.PreferredTheme = AppSettings.Current.Theme switch
        {
            "light" => TitleBarTheme.Light,
            "dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>
    /// Places the window where it was last closed, maximized if it was; the first time (or when
    /// that place is on no display any more), centers it at its default size and opens it
    /// maximized. Also sets the minimum size for the monitor scale.
    /// </summary>
    private void ApplyPlacement()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        _presenter.PreferredMinimumWidth = (int)(MinimumSize.Width * scale);
        _presenter.PreferredMinimumHeight = (int)(MinimumSize.Height * scale);
        AppWindow.SetPresenter(_presenter);

        var saved = AppSettings.Current.Window;
        var area = saved is null
            ? null
            : DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.None);
        if (saved is not null && area is not null)
        {
            // The display bounds, not its work area: a snapped window overlaps it with its invisible borders.
            var screen = area.OuterBounds;
            _restoredBounds = saved with
            {
                Width = Math.Min(saved.Width, screen.Width),
                Height = Math.Min(saved.Height, screen.Height),
            };
            _maximizeOnShow = AppSettings.Current.WindowMaximized;
        }
        else
        {
            var work = DisplayArea.Primary.WorkArea;
            var width = Math.Min((int)(DefaultSize.Width * scale), work.Width);
            var height = Math.Min((int)(DefaultSize.Height * scale), work.Height);
            _restoredBounds = new WindowBounds(
                work.X + ((work.Width - width) / 2),
                work.Y + ((work.Height - height) / 2),
                width,
                height);
            _maximizeOnShow = true;
        }

        AppWindow.MoveAndResize(new RectInt32(_restoredBounds.X, _restoredBounds.Y, _restoredBounds.Width, _restoredBounds.Height));
        AppWindow.Changed += AppWindow_Changed;
    }

    /// <summary>
    /// Follows the position and size of the window while it is shown and restored, to save them.
    /// </summary>
    /// <param name="sender">The app window.</param>
    /// <param name="args">What changed.</param>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange)
            && sender.IsVisible
            && _presenter.State == OverlappedPresenterState.Restored)
        {
            _restoredBounds = new WindowBounds(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    /// <summary>
    /// Saves the position, size and maximized state of the window for the next start.
    /// </summary>
    private void SavePlacement()
    {
        var state = _presenter.State;
        AppSettings.Current.Window = _restoredBounds;
        if (state != OverlappedPresenterState.Minimized)
        {
            // Never shown yet (started in the notification area): keep what it would have opened as.
            AppSettings.Current.WindowMaximized = _maximizeOnShow || state == OverlappedPresenterState.Maximized;
        }
        AppSettings.Save();
    }

    /// <summary>
    /// Rebuilds the navigation items: All machines, the groups, Ungrouped, then New group.
    /// </summary>
    private void BuildNavigation()
    {
        var selected = (Navigation.SelectedItem as NavigationViewItem)?.Tag as GroupSection;
        var selectedKey = _selectGroup ?? selected?.Group;
        var wasUngrouped = _selectGroup is null && selected is { Group: null };
        var keepSettings = _selectGroup is null && ReferenceEquals(Navigation.SelectedItem, Navigation.SettingsItem);
        _selectGroup = null;

        Navigation.MenuItems.Clear();
        Navigation.MenuItems.Add(_allMachines);

        var groups = _viewModel.Sections.Where(section => section.Group is not null).ToList();
        if (groups.Count > 0)
        {
            Navigation.MenuItems.Add(new NavigationViewItemHeader { Content = Strings.Get("Nav_GroupsHeader") });
        }

        NavigationViewItem? reselect = null;
        foreach (var section in _viewModel.Sections)
        {
            var item = new NavigationViewItem
            {
                Content = section.Title,
                Tag = section,
                Icon = new FontIcon { Glyph = "\uE8B7" },
                InfoBadge = new InfoBadge { Visibility = Visibility.Collapsed },
            };
            if (section.Group is not null)
            {
                item.ContextFlyout = GroupMenu(section, groups.IndexOf(section), groups.Count);
                item.KeyDown += (_, args) => GroupItem_KeyDown(section, args);

                // Centered in the gap between two items, and as wide as their highlight.
                _reorder.Attach(item, () => section, canStart: null, gap: 3, inset: 4);
            }
            Navigation.MenuItems.Add(item);
            if ((section.Group is not null && string.Equals(section.Group, selectedKey, StringComparison.OrdinalIgnoreCase))
                || (wasUngrouped && section.Group is null))
            {
                reselect = item;
            }
        }
        Navigation.MenuItems.Add(_newGroup);

        // A reload from the settings page (Check again) stays there.
        if (keepSettings)
        {
            UpdateBadges();
            return;
        }

        var target = reselect ?? _allMachines;
        if (ReferenceEquals(Navigation.SelectedItem, target))
        {
            ListPage.Show(target.Tag as GroupSection);
        }
        else
        {
            Navigation.SelectedItem = target;
        }
        UpdateBadges();
    }

    /// <summary>
    /// Builds the context menu of a group: Wake group, Rename…, Move up, Move down, Delete group….
    /// </summary>
    /// <param name="section">The group.</param>
    /// <param name="index">Its position among the groups, 0-based.</param>
    /// <param name="count">Number of groups.</param>
    /// <returns>The menu.</returns>
    private MenuFlyout GroupMenu(GroupSection section, int index, int count)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Add(string key, string glyph, Action action, bool enabled, string? shortcut = null)
        {
            var item = new MenuFlyoutItem { Text = Strings.Get(key), Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            if (shortcut is not null)
            {
                item.KeyboardAcceleratorTextOverride = shortcut;
            }
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        menu.Opening += (_, _) =>
        {
            var usable = _viewModel.CanUseCli;
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            {
                item.IsEnabled = usable && (item.Tag as Func<bool>)?.Invoke() != false;
            }
        };

        Add("GroupMenu_WakeGroup", "\uE7E8", () => _ = _viewModel.WakeAsync(section.WakeTargets, section.Title), true)
            .Tag = (Func<bool>)(() => section.CanWake);
        Add("GroupMenu_Rename", "\uE8AC", () => _ = RenameGroupAsync(section), true, "F2");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("GroupMenu_MoveUp", "\uE74A", () => _ = _viewModel.MoveGroupAsync(section, -1), index > 0, "Alt+Up")
            .Tag = (Func<bool>)(() => index > 0);
        Add("GroupMenu_MoveDown", "\uE74B", () => _ = _viewModel.MoveGroupAsync(section, 1), index < count - 1, "Alt+Down")
            .Tag = (Func<bool>)(() => index < count - 1);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("GroupMenu_Delete", "\uE74D", () => _ = DeleteGroupAsync(section), true);
        return menu;
    }

    /// <summary>
    /// Group shortcuts on a focused group item: F2 renames, Alt+Up / Alt+Down move.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <param name="args">Key pressed.</param>
    private void GroupItem_KeyDown(GroupSection section, KeyRoutedEventArgs args)
    {
        if (!_viewModel.CanUseCli)
        {
            return;
        }

        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (args.Key)
        {
            case VirtualKey.F2:
                _ = RenameGroupAsync(section);
                args.Handled = true;
                break;
            case VirtualKey.Up when alt:
                _ = _viewModel.MoveGroupAsync(section, -1);
                args.Handled = true;
                break;
            case VirtualKey.Down when alt:
                _ = _viewModel.MoveGroupAsync(section, 1);
                args.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Creates a group and shows it.
    /// </summary>
    /// <returns>A task completing when the dialog is closed.</returns>
    private async Task NewGroupAsync()
    {
        if (!_viewModel.CanUseCli)
        {
            return;
        }

        var name = await Dialogs.GroupNameAsync(Root.XamlRoot, _viewModel, renaming: null);
        if (name is not null)
        {
            _selectGroup = name;
            BuildNavigation();
        }
    }

    /// <summary>
    /// Renames a group and keeps it selected.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <returns>A task completing when the dialog is closed.</returns>
    private async Task RenameGroupAsync(GroupSection section)
    {
        var wasSelected = (Navigation.SelectedItem as NavigationViewItem)?.Tag == section;
        var name = await Dialogs.GroupNameAsync(Root.XamlRoot, _viewModel, section);
        if (name is not null && wasSelected)
        {
            _selectGroup = name;
            BuildNavigation();
        }
    }

    /// <summary>
    /// Asks, then deletes a group.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <returns>A task completing once the group is deleted or kept.</returns>
    private async Task DeleteGroupAsync(GroupSection section)
    {
        if (await Dialogs.DeleteGroupAsync(Root.XamlRoot, section) is { } removeMachines)
        {
            await _viewModel.DeleteGroupAsync(section, removeMachines);
        }
    }

    /// <summary>
    /// Shows on each group item the number of machines offline or timed out.
    /// </summary>
    private void UpdateBadges()
    {
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item is not { Tag: GroupSection section, InfoBadge: { } badge })
            {
                continue;
            }

            var count = section.NotOnlineCount;
            badge.Value = count;
            badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                item,
                count > 0 ? Strings.Plural("Nav_Group_BadgeName", count, section.Title, count) : section.Title);
        }
    }

    /// <summary>
    /// Shows the machines of the selected item.
    /// </summary>
    /// <param name="sender">The navigation view.</param>
    /// <param name="args">Selected item.</param>
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        SettingsView.Visibility = args.IsSettingsSelected ? Visibility.Visible : Visibility.Collapsed;
        ListPage.Visibility = args.IsSettingsSelected ? Visibility.Collapsed : Visibility.Visible;
        if (args.IsSettingsSelected)
        {
            SettingsView.Load();
            return;
        }
        ListPage.Show((args.SelectedItem as NavigationViewItem)?.Tag as GroupSection);
    }

    /// <summary>
    /// Opens the New group dialog from its navigation item.
    /// </summary>
    /// <param name="sender">The navigation view.</param>
    /// <param name="args">Item invoked.</param>
    private void Navigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == _newGroup)
        {
            _ = NewGroupAsync();
        }
    }

    /// <summary>
    /// Opens or closes the navigation pane from the title bar button.
    /// </summary>
    /// <param name="sender">The title bar.</param>
    /// <param name="args">Unused.</param>
    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    /// <summary>
    /// Gets the DPI of the monitor showing a window.
    /// </summary>
    /// <param name="hwnd">Window handle.</param>
    /// <returns>The DPI (96 at 100% scale).</returns>
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
