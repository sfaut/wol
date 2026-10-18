using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Wol.Desktop.Models;
using Wol.Desktop.Services;

namespace Wol.Desktop;

/// <summary>
/// Notification area part of the main window: icon, menu, balloons, close to the tray.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Icon in the notification area.
    /// </summary>
    private TrayIcon? _tray;

    /// <summary>
    /// Whether the app is quitting (closing the window then really closes it).
    /// </summary>
    private bool _quitting;

    /// <summary>
    /// Shows the window, restored and in front (tray icon, second start of Wol).
    /// </summary>
    public void ShowFromTray()
    {
        if (_maximizeOnShow)
        {
            // Maximizing also shows the window, without first showing it restored.
            _maximizeOnShow = false;
            _presenter.Maximize();
        }
        else
        {
            AppWindow.Show();
        }
        if (_presenter.State == OverlappedPresenterState.Minimized)
        {
            _presenter.Restore();
        }
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>
    /// Adds the notification area icon and follows the machine states.
    /// </summary>
    private void StartTray()
    {
        _tray = new TrayIcon(TrayStatus.Online, Strings.Get("AppDisplayName"));
        _tray.Invoked += (_, _) => ShowFromTray();
        _tray.MenuRequested = TrayMenu;
        _viewModel.StatesChanged += (_, _) => UpdateTray();
        _viewModel.PropertyChanged += (_, _) => UpdateTray();
        _viewModel.Alerted += (_, alert) => ShowAlert(alert);
    }

    /// <summary>
    /// Shows the overall state in the icon and its tooltip.
    /// </summary>
    private void UpdateTray()
    {
        if (_tray is null)
        {
            return;
        }

        var machines = _viewModel.AllMachines.ToList();
        var online = machines.Count(machine => machine.State == MachineState.Online);
        if (_viewModel.Banner is { } banner)
        {
            _tray.Update(TrayStatus.Error, Strings.Format("Tray_Tooltip_Error", banner.Title));
        }
        else if (online == machines.Count)
        {
            _tray.Update(TrayStatus.Online, Strings.Format("Tray_Tooltip_AllOnline", online));
        }
        else
        {
            _tray.Update(TrayStatus.Offline, Strings.Format("Tray_Tooltip_Mixed", online, machines.Count - online));
        }
    }

    /// <summary>
    /// Builds the notification area menu: Open, Wake ▸ groups, Refresh, Quit.
    /// </summary>
    /// <returns>The items.</returns>
    private IReadOnlyList<TrayMenuItem?> TrayMenu()
    {
        var wake = new List<TrayMenuItem?>();
        var offline = _viewModel.AllMachines.Where(machine => machine.State != MachineState.Online && machine.State != MachineState.Waking).ToList();
        wake.Add(new TrayMenuItem(
            Strings.Format("Tray_Menu_WakeAll", offline.Count),
            offline.Count > 0 ? () => _ = _viewModel.WakeAsync(offline) : null));
        wake.Add(null);
        foreach (var section in _viewModel.Sections.Where(section => section.Machines.Count > 0))
        {
            var sleeping = section.Machines.Where(machine => machine.State != MachineState.Online && machine.State != MachineState.Waking).ToList();
            wake.Add(sleeping.Count == 0
                ? new TrayMenuItem(Strings.Format("Tray_Menu_GroupAllOnline", section.Title), Enabled: false)
                : new TrayMenuItem(section.Title, () => _ = _viewModel.WakeAsync(sleeping, section.Title)));
        }

        return
        [
            new TrayMenuItem(Strings.Get("Tray_Menu_Open"), ShowFromTray, IsDefault: true),
            null,
            new TrayMenuItem(Strings.Get("Tray_Menu_Wake"), Enabled: _viewModel.CanUseCli && _viewModel.HasMachines, Children: wake),
            new TrayMenuItem(Strings.Get("Tray_Menu_Refresh"), _viewModel.CanUseCli ? () => _ = _viewModel.RefreshAsync() : null),
            null,
            new TrayMenuItem(Strings.Get("Tray_Menu_Quit"), Quit),
        ];
    }

    /// <summary>
    /// Shows an alert as a balloon when the window is not in front and its kind is turned on.
    /// </summary>
    /// <param name="alert">The alert.</param>
    private void ShowAlert(Alert alert)
    {
        var settings = AppSettings.Current;
        var wanted = alert.Kind switch
        {
            AlertKind.Online => settings.NotifyOnline,
            AlertKind.Offline => settings.NotifyOffline,
            AlertKind.TimedOut => settings.NotifyTimedOut,
            _ => settings.NotifyError,
        };
        if (_isActive || !settings.Notify || !wanted)
        {
            return;
        }
        _tray?.ShowBalloon(alert.Title, alert.Text, alert.Kind is AlertKind.TimedOut or AlertKind.Error);
    }

    /// <summary>
    /// Hides the window instead of closing it when "Close to the notification area" is on.
    /// </summary>
    /// <param name="sender">The app window.</param>
    /// <param name="args">Lets the close be cancelled.</param>
    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_quitting && AppSettings.Current.CloseToTray)
        {
            args.Cancel = true;
            SavePlacement();
            AppWindow.Hide();
            if (!AppSettings.Current.StillRunningShown)
            {
                AppSettings.Current.StillRunningShown = true;
                AppSettings.Save();
                _tray?.ShowBalloon(Strings.Get("Balloon_StillRunning_Title"), Strings.Get("Balloon_StillRunning_Text"));
            }
            return;
        }

        Quit();
    }

    /// <summary>
    /// Stops monitoring, removes the icon and ends the app.
    /// </summary>
    private void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        SavePlacement();
        _viewModel.Shutdown();
        _tray?.Dispose();
        _tray = null;
        Close();
        Application.Current.Exit();
    }

    /// <summary>
    /// Win32 <c>SetForegroundWindow</c> (user32.dll).
    /// </summary>
    /// <param name="hwnd">Window to bring to the foreground.</param>
    /// <returns>True on success.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
