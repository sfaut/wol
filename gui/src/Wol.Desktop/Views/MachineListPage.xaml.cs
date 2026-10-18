using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wol.Desktop.Models;
using Wol.Desktop.Services;
using Wol.Desktop.ViewModels;

namespace Wol.Desktop.Views;

/// <summary>
/// Machine list of every group, or of one group.
/// </summary>
public sealed partial class MachineListPage : UserControl
{
    /// <summary>
    /// Most notifications shown at once; the oldest one closes first.
    /// </summary>
    private const int MaxNotifications = 3;

    /// <summary>
    /// How long a success notification stays.
    /// </summary>
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// App state shown by the page.
    /// </summary>
    private MainViewModel? _viewModel;

    /// <summary>
    /// Section shown alone; every section when null.
    /// </summary>
    private GroupSection? _only;

    /// <summary>
    /// Empty state shown, if any.
    /// </summary>
    private EmptyKind _empty;

    /// <summary>
    /// Builds the page.
    /// </summary>
    public MachineListPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// App state shown by the page.
    /// </summary>
    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.StatesChanged -= ViewModel_StatesChanged;
                _viewModel.Notified -= ViewModel_Notified;
            }

            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                _viewModel.StatesChanged += ViewModel_StatesChanged;
                _viewModel.Notified += ViewModel_Notified;
            }
            Show(null);
        }
    }

    /// <summary>
    /// Gives a group card its section.
    /// </summary>
    /// <param name="sender">The repeater.</param>
    /// <param name="args">The card and its index.</param>
    private void Sections_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Expander card)
        {
            return;
        }

        // The repeater leaves the data context of the card empty; its buttons read it.
        card.DataContext = sender.ItemsSourceView.GetAt(args.Index);
    }

    /// <summary>
    /// Shows every section, or one section alone.
    /// </summary>
    /// <param name="only">Section to show alone; every section when null.</param>
    public void Show(GroupSection? only)
    {
        _only = only;
        var sections = _viewModel?.Sections.Where(section => only is null || section == only).ToList() ?? [];
        SectionsRepeater.ItemsSource = sections;
        TitleText.Text = only?.Title ?? Strings.Get("Page_AllMachines_Title");
        SearchBox.PlaceholderText = only is null
            ? Strings.Get("Search_Placeholder")
            : Strings.Format("Search_PlaceholderInGroup", only.Title);
        UpdateHeader();
        UpdateBanner();
    }

    /// <summary>
    /// Refreshes the summary, the empty state and the commands.
    /// </summary>
    private void UpdateHeader()
    {
        var sections = (_only is null ? _viewModel?.Sections.ToList() : [_only]) ?? [];
        SummaryText.Text = GroupSection.Summarize(sections.SelectMany(section => section.Machines));
        RefreshButton.IsEnabled = _viewModel?.CanUseCli == true;
        AddButton.IsEnabled = _viewModel?.CanUseCli == true;

        // Empty states, by precedence: no machine at all, empty group, nothing matches.
        _empty = _viewModel is null ? EmptyKind.None
            : _only is null && !_viewModel.HasMachines ? EmptyKind.First
            : _only is { Machines.Count: 0 } ? EmptyKind.Group
            : SearchBox.Text.Trim().Length > 0 && sections.All(section => section.Shown.Count == 0) ? EmptyKind.Search
            : EmptyKind.None;
        EmptyState.Visibility = _empty == EmptyKind.None ? Visibility.Collapsed : Visibility.Visible;
        SectionsRepeater.Visibility = _empty is EmptyKind.None or EmptyKind.Search ? Visibility.Visible : Visibility.Collapsed;
        EmptySecondaryButton.Visibility = _empty == EmptyKind.Search ? Visibility.Collapsed : Visibility.Visible;
        EmptyPrimaryButton.IsEnabled = _empty == EmptyKind.Search || _viewModel?.CanUseCli == true;
        EmptySecondaryButton.IsEnabled = _empty == EmptyKind.Group || _viewModel?.CanUseCli == true;
        switch (_empty)
        {
            case EmptyKind.First:
                EmptyTitle.Text = Strings.Get("Empty_First_Title");
                EmptyBody.Text = Strings.Get("Empty_First_Body");
                EmptyPrimaryButton.Content = Strings.Get("Empty_First_Add");
                EmptySecondaryButton.Content = Strings.Get("Empty_First_CreateGroup");
                break;
            case EmptyKind.Group:
                EmptyTitle.Text = Strings.Format("Empty_Group_Title", _only!.Title);
                EmptyBody.Text = Strings.Get("Empty_Group_Body");
                EmptyPrimaryButton.Content = Strings.Get("Empty_First_Add");
                EmptySecondaryButton.Content = Strings.Get("Empty_Group_GoToAll");
                break;
            case EmptyKind.Search:
                EmptyTitle.Text = Strings.Format("Empty_Search_Title", SearchBox.Text.Trim());
                EmptyBody.Text = Strings.Get("Empty_Search_Body");
                EmptyPrimaryButton.Content = Strings.Get("Empty_Search_Clear");
                break;
        }
    }

    /// <summary>
    /// Raised when the page asks to show every machine (empty group page).
    /// </summary>
    public event EventHandler? ShowAllRequested;

    /// <summary>
    /// Opens the Add machine dialog, with the group of the page pre-filled (Ctrl+N).
    /// </summary>
    /// <returns>A task completing when the dialog is closed.</returns>
    public async Task AddMachineAsync()
    {
        if (_viewModel is { CanUseCli: true })
        {
            await new MachineDialog(_viewModel, editing: null, group: _only?.Group) { XamlRoot = XamlRoot }.ShowAsync();
        }
    }

    /// <summary>
    /// Opens the New group dialog (Ctrl+Shift+N).
    /// </summary>
    /// <returns>The group created, or null.</returns>
    public async Task<string?> AddGroupAsync() =>
        _viewModel is { CanUseCli: true } ? await Dialogs.GroupNameAsync(XamlRoot, _viewModel, renaming: null) : null;

    /// <summary>
    /// Opens the Edit machine dialog.
    /// </summary>
    /// <param name="machine">Machine to edit.</param>
    /// <returns>A task completing when the dialog is closed.</returns>
    private async Task EditAsync(MachineItem machine)
    {
        if (_viewModel is { CanUseCli: true })
        {
            await new MachineDialog(_viewModel, machine) { XamlRoot = XamlRoot }.ShowAsync();
        }
    }

    /// <summary>
    /// Asks, then removes a machine.
    /// </summary>
    /// <param name="machine">Machine to remove.</param>
    /// <returns>A task completing once it is removed or kept.</returns>
    private async Task RemoveAsync(MachineItem machine)
    {
        if (_viewModel is { CanUseCli: true } && await Dialogs.RemoveMachinesAsync(XamlRoot, [machine]))
        {
            await _viewModel.RemoveMachinesAsync([machine]);
        }
    }

    /// <summary>
    /// Fills the More menu of a row: Wake, Check now, Edit, Duplicate, Move to group,
    /// Copy MAC address, Remove.
    /// </summary>
    /// <param name="sender">The menu.</param>
    /// <param name="args">Unused.</param>
    private void RowMenu_Opening(object? sender, object args)
    {
        if (sender is not MenuFlyout { Target: FrameworkElement { DataContext: MachineItem machine } } menu
            || _viewModel is not { } viewModel)
        {
            return;
        }

        var enabled = viewModel.CanUseCli;
        MenuFlyoutItem Item(string key, string glyph, Action action, bool isEnabled = true)
        {
            var item = new MenuFlyoutItem
            {
                Text = Strings.Get(key),
                Icon = new FontIcon { Glyph = glyph },
                IsEnabled = isEnabled && enabled,
            };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Clear();
        menu.Items.Add(Item("RowMenu_Wake", "\uE7E8", () => _ = viewModel.WakeAsync([machine]),
            machine.State is not (MachineState.Online or MachineState.Waking or MachineState.Error)));
        menu.Items.Add(Item("RowMenu_CheckNow", "\uE72C", () => _ = viewModel.CheckAsync([machine]),
            machine.State != MachineState.Waking));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("RowMenu_Edit", "\uE70F", () => _ = EditAsync(machine)));
        menu.Items.Add(Item("RowMenu_Duplicate", "\uE8C8", () => _ = new MachineDialog(viewModel, editing: null, machine.Definition)
        {
            XamlRoot = XamlRoot,
        }.ShowAsync()));

        // Move to group: groups in order, then Ungrouped, then New group…
        var move = new MenuFlyoutSubItem
        {
            Text = Strings.Get("RowMenu_MoveToGroup"),
            Icon = new FontIcon { Glyph = "\uE8DE" },
            IsEnabled = enabled,
        };
        foreach (var group in viewModel.GroupNames.Cast<string?>().Append(null))
        {
            var current = string.Equals(group, machine.Definition.Group, StringComparison.OrdinalIgnoreCase);
            var target = new MenuFlyoutItem
            {
                Text = group ?? Strings.Get("Nav_Ungrouped"),
                Icon = current ? new FontIcon { Glyph = "\uE73E" } : null,
                IsEnabled = !current,
            };
            target.Click += (_, _) => _ = viewModel.MoveMachinesAsync([machine], group);
            move.Items.Add(target);
        }
        move.Items.Add(new MenuFlyoutSeparator());
        var newGroup = new MenuFlyoutItem { Text = Strings.Get("RowMenu_MoveToGroup_NewGroup") };
        newGroup.Click += async (_, _) =>
        {
            if (await AddGroupAsync() is { } created)
            {
                await viewModel.MoveMachinesAsync([machine], created);
            }
        };
        move.Items.Add(newGroup);
        menu.Items.Add(move);

        // Copy MAC address: one item, or a submenu for several interfaces.
        var interfaces = machine.Definition.Interfaces;
        if (interfaces.Count == 1)
        {
            menu.Items.Add(Item("RowMenu_CopyMac", "\uE8C8", () => Copy(interfaces[0].Mac), isEnabled: true));
            ((MenuFlyoutItem)menu.Items[^1]).IsEnabled = true;
        }
        else
        {
            var copy = new MenuFlyoutSubItem { Text = Strings.Get("RowMenu_CopyMacs"), Icon = new FontIcon { Glyph = "\uE8C8" } };
            foreach (var item in interfaces)
            {
                var entry = new MenuFlyoutItem
                {
                    Text = item.Label is { } label ? Strings.Format("RowMenu_CopyMac_Item", label, item.Mac) : item.Mac,
                };
                entry.Click += (_, _) => Copy(item.Mac);
                copy.Items.Add(entry);
            }
            var all = new MenuFlyoutItem { Text = Strings.Get("RowMenu_CopyMac_All") };
            all.Click += (_, _) => Copy(string.Join(Environment.NewLine, interfaces.Select(item => item.Mac)));
            copy.Items.Add(all);
            menu.Items.Add(copy);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("RowMenu_Remove", "\uE74D", () => _ = RemoveAsync(machine)));
    }

    /// <summary>
    /// Copies text to the clipboard.
    /// </summary>
    /// <param name="text">Text to copy.</param>
    private static void Copy(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    /// <summary>
    /// Edits a machine on double click.
    /// </summary>
    /// <param name="sender">The row.</param>
    /// <param name="args">Unused.</param>
    private void Row_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: MachineItem machine })
        {
            _ = EditAsync(machine);
        }
    }

    /// <summary>
    /// Enter edits the focused machine, Delete removes it.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="args">Key pressed.</param>
    private void Rows_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.OriginalSource is not ListViewItem { Content: MachineItem machine })
        {
            return;
        }

        switch (args.Key)
        {
            case Windows.System.VirtualKey.Enter:
                _ = EditAsync(machine);
                args.Handled = true;
                break;
            case Windows.System.VirtualKey.Delete:
                _ = RemoveAsync(machine);
                args.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Adds a machine (Ctrl+N).
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="args">Unused.</param>
    private void Add_Click(object sender, RoutedEventArgs args) => _ = AddMachineAsync();

    /// <summary>
    /// Main action of the empty state: add a machine, or clear the search.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="args">Unused.</param>
    private void EmptyPrimary_Click(object sender, RoutedEventArgs args)
    {
        if (_empty == EmptyKind.Search)
        {
            SearchBox.Text = string.Empty;
            return;
        }
        _ = AddMachineAsync();
    }

    /// <summary>
    /// Second action of the empty state: create a group, or show every machine.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="args">Unused.</param>
    private void EmptySecondary_Click(object sender, RoutedEventArgs args)
    {
        if (_empty == EmptyKind.Group)
        {
            ShowAllRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _ = AddGroupAsync();
    }

    /// <summary>
    /// Empty state shown.
    /// </summary>
    private enum EmptyKind
    {
        /// <summary>
        /// The page has machines to show.
        /// </summary>
        None,

        /// <summary>
        /// No machine at all yet.
        /// </summary>
        First,

        /// <summary>
        /// The group shown has no machines.
        /// </summary>
        Group,

        /// <summary>
        /// No machine matches the search.
        /// </summary>
        Search,
    }

    /// <summary>
    /// Shows the degraded state of the app, with its actions.
    /// </summary>
    private void UpdateBanner()
    {
        if (_viewModel?.Banner is not { } banner)
        {
            BannerBar.IsOpen = false;
            return;
        }

        BannerBar.Title = banner.Title;
        BannerBar.Message = banner.Message;
        BannerBar.ActionButton = banner.Kind switch
        {
            BannerKind.CliMissing or BannerKind.CliIncompatible => ActionButton(
                Strings.Get("Error_CliMissing_CheckAgain"),
                () => _ = _viewModel.StartAsync()),
            BannerKind.ConfigInvalid when _viewModel.ConfigErrorPath is { } path => ActionButton(
                Strings.Get("Error_Config_Open"),
                () => Open(path)),
            BannerKind.NoNetwork => ActionButton(
                Strings.Get("Error_NoNetwork_Settings"),
                () => Open("ms-settings:network")),
            _ => null,
        };
        BannerBar.IsOpen = true;
    }

    /// <summary>
    /// Builds an InfoBar action button.
    /// </summary>
    /// <param name="text">Button text.</param>
    /// <param name="action">What the button does.</param>
    /// <returns>The button.</returns>
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// Opens a file or a URI with its default application.
    /// </summary>
    /// <param name="target">File path or URI.</param>
    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // Nothing to open it with: nothing to do.
        }
    }

    /// <summary>
    /// Shows a notification at the bottom of the page.
    /// </summary>
    /// <param name="notification">Notification to show.</param>
    private void ShowNotification(Notification notification)
    {
        var bar = new InfoBar
        {
            Title = notification.Title,
            Message = notification.Message,
            Severity = notification.Severity,
            IsClosable = true,
            IsOpen = true,
        };
        if (notification is { ActionText: { } text, Action: { } action })
        {
            bar.ActionButton = ActionButton(text, () =>
            {
                NotificationHost.Children.Remove(bar);
                action();
            });
        }
        bar.Closed += (_, _) => NotificationHost.Children.Remove(bar);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            bar,
            notification.Severity == InfoBarSeverity.Success
                ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite
                : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

        while (NotificationHost.Children.Count >= MaxNotifications)
        {
            NotificationHost.Children.RemoveAt(0);
        }
        NotificationHost.Children.Add(bar);

        if (notification.AutoClose)
        {
            var hovered = false;
            bar.PointerEntered += (_, _) => hovered = true;
            bar.PointerExited += (_, _) => hovered = false;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = AutoCloseDelay;
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                // Paused while the user is reading or using it.
                if (hovered || bar.FocusState != FocusState.Unfocused)
                {
                    timer.Start();
                    return;
                }
                NotificationHost.Children.Remove(bar);
            };
            timer.Start();
        }
    }

    /// <summary>
    /// Follows the banner and the machine count.
    /// </summary>
    /// <param name="sender">The view model.</param>
    /// <param name="args">Changed property.</param>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        UpdateBanner();
        UpdateHeader();
    }

    /// <summary>
    /// Follows machine states.
    /// </summary>
    /// <param name="sender">The view model.</param>
    /// <param name="args">Unused.</param>
    private void ViewModel_StatesChanged(object? sender, EventArgs args) => UpdateHeader();

    /// <summary>
    /// Shows a notification of the view model.
    /// </summary>
    /// <param name="sender">The view model.</param>
    /// <param name="notification">Notification to show.</param>
    private void ViewModel_Notified(object? sender, Notification notification) => ShowNotification(notification);

    /// <summary>
    /// Checks every machine now (F5).
    /// </summary>
    /// <param name="sender">The Refresh button.</param>
    /// <param name="args">Unused.</param>
    private void Refresh_Click(object sender, RoutedEventArgs args) => _ = _viewModel?.RefreshAsync();

    /// <summary>
    /// Filters the machines as the search text changes.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="args">Unused.</param>
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _viewModel?.ApplySearch(sender.Text);
        UpdateHeader();
    }

    /// <summary>
    /// Focuses the search box (Ctrl+F).
    /// </summary>
    /// <param name="sender">The accelerator.</param>
    /// <param name="args">Marked as handled.</param>
    private void Search_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    /// <summary>
    /// Wakes, or retries waking, an offline or timed-out machine.
    /// </summary>
    /// <param name="sender">The row button.</param>
    /// <param name="args">Unused.</param>
    private void AccentWake_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: MachineItem machine })
        {
            _ = _viewModel?.WakeAsync([machine]);
        }
    }

    /// <summary>
    /// Wakes a machine whose state is unknown, or stops waiting for a waking one.
    /// </summary>
    /// <param name="sender">The row button.</param>
    /// <param name="args">Unused.</param>
    private void StandardWake_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: MachineItem machine } || _viewModel is null)
        {
            return;
        }

        if (machine.State == MachineState.Waking)
        {
            _viewModel.CancelWake(machine);
        }
        else
        {
            _ = _viewModel.WakeAsync([machine]);
        }
    }

    /// <summary>
    /// Wakes every machine of a section that is not online.
    /// </summary>
    /// <param name="sender">The section button.</param>
    /// <param name="args">Unused.</param>
    private void WakeGroup_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: GroupSection section })
        {
            _ = _viewModel?.WakeAsync(section.WakeTargets, section.Title);
        }
    }
}

