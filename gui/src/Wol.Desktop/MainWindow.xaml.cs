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
using Windows.ApplicationModel.DataTransfer;
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
    /// Group being dragged in the navigation pane to reorder it, if any.
    /// </summary>
    private GroupSection? _draggedGroup;

    /// <summary>
    /// Where the left button went down on a group item, until a drag starts or the button is released.
    /// </summary>
    private Windows.Foundation.Point? _dragOrigin;

    /// <summary>
    /// Distance the pointer moves, in pixels, before pressing a group item becomes a drag.
    /// </summary>
    private const double DragThreshold = 4;

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
        ApplySize();
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
    /// Sizes the window for the monitor scale and sets its minimum size.
    /// </summary>
    private void ApplySize()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultSize.Width * scale), (int)(DefaultSize.Height * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinimumSize.Width * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumSize.Height * scale);
        }
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
                item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(GroupItem_PointerPressed), handledEventsToo: true);
                item.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(GroupItem_PointerMoved), handledEventsToo: true);
                item.AllowDrop = true;
                item.DragStarting += (_, args) => GroupItem_DragStarting(section, args);
                item.DropCompleted += (_, _) =>
                {
                    _draggedGroup = null;
                    ShowDropMarker(item, before: null);
                };
                item.DragOver += (_, args) => GroupItem_DragOver(item, section, args);
                item.DragLeave += (_, _) => ShowDropMarker(item, before: null);
                item.Drop += (_, args) => GroupItem_Drop(item, section, args);
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
    /// Remembers where the left button went down on a group item, to start a drag when it moves.
    /// </summary>
    /// <param name="sender">The navigation item.</param>
    /// <param name="args">Pointer.</param>
    private void GroupItem_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint((UIElement)sender);
        _dragOrigin = point.Properties.IsLeftButtonPressed ? point.Position : null;
    }

    /// <summary>
    /// Starts dragging a group item once the pointer moved far enough with the button down. The
    /// item captures the pointer to select itself, which keeps <c>CanDrag</c> from ever starting.
    /// </summary>
    /// <param name="sender">The navigation item.</param>
    /// <param name="args">Pointer.</param>
    private void GroupItem_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        var item = (UIElement)sender;
        var point = args.GetCurrentPoint(item);
        if (_dragOrigin is not { } origin || !point.Properties.IsLeftButtonPressed)
        {
            _dragOrigin = null;
            return;
        }

        if (Math.Abs(point.Position.X - origin.X) > DragThreshold || Math.Abs(point.Position.Y - origin.Y) > DragThreshold)
        {
            _dragOrigin = null;
            _ = item.StartDragAsync(point);
        }
    }

    /// <summary>
    /// Starts dragging a group to reorder it.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <param name="args">Drag data; cancelled when the CLI cannot be used.</param>
    private void GroupItem_DragStarting(GroupSection section, DragStartingEventArgs args)
    {
        if (!_viewModel.CanUseCli)
        {
            args.Cancel = true;
            return;
        }

        _draggedGroup = section;
        args.Data.SetText(section.Title);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    /// <summary>
    /// Accepts a dragged group over another one and shows where it would land.
    /// </summary>
    /// <param name="item">Navigation item under the pointer.</param>
    /// <param name="section">Its group.</param>
    /// <param name="args">Pointer position; operation accepted.</param>
    private void GroupItem_DragOver(NavigationViewItem item, GroupSection section, DragEventArgs args)
    {
        var before = args.GetPosition(item).Y < item.ActualHeight / 2;
        if (DropPosition(section, before) is null)
        {
            args.AcceptedOperation = DataPackageOperation.None;
            ShowDropMarker(item, before: null);
            return;
        }

        args.AcceptedOperation = DataPackageOperation.Move;
        args.DragUIOverride.IsCaptionVisible = false;
        args.DragUIOverride.IsGlyphVisible = false;
        ShowDropMarker(item, before);
    }

    /// <summary>
    /// Moves the dragged group above or below the group it is dropped on.
    /// </summary>
    /// <param name="item">Navigation item dropped on.</param>
    /// <param name="section">Its group.</param>
    /// <param name="args">Pointer position.</param>
    private void GroupItem_Drop(NavigationViewItem item, GroupSection section, DragEventArgs args)
    {
        ShowDropMarker(item, before: null);
        var position = DropPosition(section, args.GetPosition(item).Y < item.ActualHeight / 2);
        if (_draggedGroup is { } dragged && position is not null)
        {
            _ = _viewModel.MoveGroupToAsync(dragged, position.Value);
        }
        _draggedGroup = null;
    }

    /// <summary>
    /// Computes where the dragged group lands when dropped next to a group.
    /// </summary>
    /// <param name="target">Group under the pointer.</param>
    /// <param name="before">Whether the drop is on the upper half of the target (above it).</param>
    /// <returns>The new 1-based position, or null when no group is dragged or it would not move.</returns>
    private int? DropPosition(GroupSection target, bool before)
    {
        if (_draggedGroup is not { } dragged || dragged == target)
        {
            return null;
        }

        var groups = _viewModel.Sections.Where(section => section.Group is not null).ToList();
        var from = groups.IndexOf(dragged);
        var to = groups.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return null;
        }

        // Index of the target once the dragged group is taken out of the list.
        var index = to > from ? to - 1 : to;
        var position = (before ? index : index + 1) + 1;
        return position == from + 1 ? null : position;
    }

    /// <summary>
    /// Draws an accent line above or below a group item, where a dragged group would land. The
    /// line is an overlay: the item's own border is reset by its pointer-over visual state.
    /// </summary>
    /// <param name="item">The navigation item.</param>
    /// <param name="before">True above, false below, null to remove the line.</param>
    private void ShowDropMarker(NavigationViewItem item, bool? before)
    {
        if (before is not { } above)
        {
            DropMarker.Visibility = Visibility.Collapsed;
            return;
        }

        // Centered in the gap between two items, and as wide as their highlight.
        const double gap = 3;
        const double inset = 4;
        var origin = item.TransformToVisual(DropLayer).TransformPoint(default);
        DropMarker.Width = Math.Max(0, item.ActualWidth - 2 * inset);
        Canvas.SetLeft(DropMarker, origin.X + inset);
        Canvas.SetTop(DropMarker, (above ? origin.Y - gap : origin.Y + item.ActualHeight + gap) - DropMarker.Height / 2);
        DropMarker.Visibility = Visibility.Visible;
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
