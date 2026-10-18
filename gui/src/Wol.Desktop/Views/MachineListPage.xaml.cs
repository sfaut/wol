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
        var machines = (_only is null ? _viewModel?.Sections : [_only])?.SelectMany(section => section.Machines) ?? [];
        SummaryText.Text = GroupSection.Summarize(machines);
        EmptyState.Visibility = _viewModel is { HasMachines: false } && _only is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshButton.IsEnabled = _viewModel?.CanUseCli == true;
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
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        _viewModel?.ApplySearch(sender.Text);

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

