using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wol.Desktop.Models;
using Wol.Desktop.Services;
using Wol.Desktop.ViewModels;
using Windows.Graphics;

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
    /// App state, fed by the CLI.
    /// </summary>
    private readonly MainViewModel _viewModel;

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
            Icon = new FontIcon { Glyph = "" },
        };

        _viewModel = new MainViewModel(DispatcherQueue);
        _viewModel.GroupsChanged += (_, _) => BuildNavigation();
        _viewModel.StatesChanged += (_, _) => UpdateBadges();
        ListPage.ViewModel = _viewModel;
        BuildNavigation();

        Activated += Window_Activated;
        Closed += (_, _) => _viewModel.Shutdown();
    }

    /// <summary>
    /// Loads the machines once, when the window is first shown.
    /// </summary>
    /// <param name="sender">The window.</param>
    /// <param name="args">Unused.</param>
    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= Window_Activated;
        _ = _viewModel.StartAsync();
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
    /// Rebuilds the navigation items: All machines, then the groups, then Ungrouped.
    /// </summary>
    private void BuildNavigation()
    {
        var selected = (Navigation.SelectedItem as NavigationViewItem)?.Tag as GroupSection;
        var selectedKey = selected?.Group;
        var wasUngrouped = selected is { Group: null };

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
                Icon = new FontIcon { Glyph = "" },
                InfoBadge = new InfoBadge { Visibility = Visibility.Collapsed },
            };
            Navigation.MenuItems.Add(item);
            if ((section.Group is not null && string.Equals(section.Group, selectedKey, StringComparison.OrdinalIgnoreCase))
                || (wasUngrouped && section.Group is null))
            {
                reselect = item;
            }
        }

        Navigation.SelectedItem = reselect ?? _allMachines;
        UpdateBadges();
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
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) =>
        ListPage.Show((args.SelectedItem as NavigationViewItem)?.Tag as GroupSection);

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
