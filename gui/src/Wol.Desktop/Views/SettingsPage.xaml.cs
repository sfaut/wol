using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wol.Desktop.Services;
using Wol.Desktop.ViewModels;

namespace Wol.Desktop.Views;

/// <summary>
/// Settings: every change is saved at once in <see cref="AppSettings"/>.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    /// <summary>
    /// Values of the theme box, in order.
    /// </summary>
    private static readonly string[] Themes = ["system", "light", "dark"];

    /// <summary>
    /// Values of the language box, in order.
    /// </summary>
    private static readonly string[] Languages = ["system", "en-US", "fr-FR"];

    /// <summary>
    /// App state, for the CLI status and the configuration file.
    /// </summary>
    private MainViewModel? _viewModel;

    /// <summary>
    /// Whether the controls are being filled from the settings (their events are ignored).
    /// </summary>
    private bool _loading;

    /// <summary>
    /// Builds the page and fills it from the settings.
    /// </summary>
    public SettingsPage()
    {
        InitializeComponent();

        ThemeBox.Items.Add(Strings.Get("Settings_Theme_System"));
        ThemeBox.Items.Add(Strings.Get("Settings_Theme_Light"));
        ThemeBox.Items.Add(Strings.Get("Settings_Theme_Dark"));

        // Language names are never translated.
        LanguageBox.Items.Add(Strings.Get("Settings_Language_System"));
        LanguageBox.Items.Add("English");
        LanguageBox.Items.Add("Français");

        AppIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "wol-gui.ico")));
        VersionText.Text = Strings.Format("Settings_About_Version", Version);
        Load();
    }

    /// <summary>
    /// Raised when the theme changed, to apply it at once.
    /// </summary>
    public event EventHandler? ThemeChanged;

    /// <summary>
    /// Version of the app, e.g. "0.1.0".
    /// </summary>
    public static string Version { get; } =
        (typeof(SettingsPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];

    /// <summary>
    /// App state, for the CLI status and the configuration file.
    /// </summary>
    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            }
            UpdateAbout();
        }
    }

    /// <summary>
    /// Fills the controls from the settings (and the Startup folder).
    /// </summary>
    public void Load()
    {
        _loading = true;
        var settings = AppSettings.Current;
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, settings.Theme));
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));
        StartWithWindowsSwitch.IsOn = StartupShortcut.Exists;
        StartMinimizedSwitch.IsOn = settings.StartMinimized;
        CloseToTraySwitch.IsOn = settings.CloseToTray;
        CheckIntervalBox.Value = settings.CheckInterval;
        WakeTimeoutBox.Value = settings.WakeTimeout;
        NotifySwitch.IsOn = settings.Notify;
        NotifyOnlineBox.IsChecked = settings.NotifyOnline;
        NotifyOfflineBox.IsChecked = settings.NotifyOffline;
        NotifyTimedOutBox.IsChecked = settings.NotifyTimedOut;
        NotifyErrorBox.IsChecked = settings.NotifyError;
        UpdateNotifyBoxes();
        UpdateAbout();
        _loading = false;
    }

    /// <summary>
    /// Enables the notification kinds only while notifications are on.
    /// </summary>
    private void UpdateNotifyBoxes()
    {
        foreach (var box in new[] { NotifyOnlineBox, NotifyOfflineBox, NotifyTimedOutBox, NotifyErrorBox })
        {
            box.IsEnabled = NotifySwitch.IsOn;
        }
    }

    /// <summary>
    /// Shows the CLI status and the configuration file.
    /// </summary>
    private void UpdateAbout()
    {
        if (_viewModel is null)
        {
            return;
        }

        CliDescription.Text = Strings.Format("Settings_About_Cli_Description", _viewModel.CliVersion ?? "?");
        CliPathText.Text = _viewModel.CliPath;
        var (glyph, brush, key) = _viewModel.CliStatus switch
        {
            "missing" => ("\uEA39", "SystemFillColorCriticalBrush", "Settings_About_Cli_NotFound"),
            "incompatible" => ("\uEA39", "SystemFillColorCriticalBrush", "Settings_About_Cli_Incompatible"),
            _ => ("\uEC61", "SystemFillColorSuccessBrush", "Settings_About_Cli_Connected"),
        };
        CliStatusIcon.Glyph = glyph;
        CliStatusIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush];
        CliStatusText.Text = Strings.Get(key);
        ConfigPathText.Text = _viewModel.ConfigPath ?? string.Empty;
        OpenConfigButton.IsEnabled = _viewModel.ConfigPath is not null;
    }

    /// <summary>
    /// Follows the CLI status.
    /// </summary>
    /// <param name="sender">The view model.</param>
    /// <param name="args">Unused.</param>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args) => UpdateAbout();

    /// <summary>
    /// Applies and saves the theme.
    /// </summary>
    /// <param name="sender">The theme box.</param>
    /// <param name="args">Unused.</param>
    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || ThemeBox.SelectedIndex < 0)
        {
            return;
        }
        AppSettings.Current.Theme = Themes[ThemeBox.SelectedIndex];
        AppSettings.Save();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves the language, applied at the next start.
    /// </summary>
    /// <param name="sender">The language box.</param>
    /// <param name="args">Unused.</param>
    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || LanguageBox.SelectedIndex < 0)
        {
            return;
        }
        AppSettings.Current.Language = Languages[LanguageBox.SelectedIndex];
        AppSettings.Save();
    }

    /// <summary>
    /// Creates or deletes the Startup shortcut.
    /// </summary>
    /// <param name="sender">The switch.</param>
    /// <param name="args">Unused.</param>
    private void StartWithWindows_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }
        StartupShortcut.Set(StartWithWindowsSwitch.IsOn);

        // Show what the Startup folder holds, in case the change failed.
        _loading = true;
        StartWithWindowsSwitch.IsOn = StartupShortcut.Exists;
        _loading = false;
    }

    /// <summary>
    /// Saves "Start minimized".
    /// </summary>
    /// <param name="sender">The switch.</param>
    /// <param name="args">Unused.</param>
    private void StartMinimized_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            AppSettings.Current.StartMinimized = StartMinimizedSwitch.IsOn;
            AppSettings.Save();
        }
    }

    /// <summary>
    /// Saves "Close to the notification area".
    /// </summary>
    /// <param name="sender">The switch.</param>
    /// <param name="args">Unused.</param>
    private void CloseToTray_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            AppSettings.Current.CloseToTray = CloseToTraySwitch.IsOn;
            AppSettings.Save();
        }
    }

    /// <summary>
    /// Applies and saves the check interval.
    /// </summary>
    /// <param name="sender">The number box.</param>
    /// <param name="args">Unused.</param>
    private void CheckInterval_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
        {
            return;
        }
        var seconds = (int)Math.Clamp(Math.Round(sender.Value), AppSettings.MinCheckInterval, AppSettings.MaxCheckInterval);
        AppSettings.Current.CheckInterval = seconds;
        AppSettings.Save();
        if (_viewModel is not null)
        {
            _viewModel.CheckInterval = seconds;
        }
    }

    /// <summary>
    /// Applies and saves the wake timeout.
    /// </summary>
    /// <param name="sender">The number box.</param>
    /// <param name="args">Unused.</param>
    private void WakeTimeout_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
        {
            return;
        }
        var seconds = (int)Math.Clamp(Math.Round(sender.Value), AppSettings.MinWakeTimeout, AppSettings.MaxWakeTimeout);
        AppSettings.Current.WakeTimeout = seconds;
        AppSettings.Save();
        if (_viewModel is not null)
        {
            _viewModel.WakeTimeout = TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>
    /// Saves the notification master switch.
    /// </summary>
    /// <param name="sender">The switch.</param>
    /// <param name="args">Unused.</param>
    private void Notify_Toggled(object sender, RoutedEventArgs args)
    {
        UpdateNotifyBoxes();
        if (!_loading)
        {
            AppSettings.Current.Notify = NotifySwitch.IsOn;
            AppSettings.Save();
        }
    }

    /// <summary>
    /// Saves the notification kinds.
    /// </summary>
    /// <param name="sender">A check box.</param>
    /// <param name="args">Unused.</param>
    private void NotifyKind_Click(object sender, RoutedEventArgs args)
    {
        var settings = AppSettings.Current;
        settings.NotifyOnline = NotifyOnlineBox.IsChecked == true;
        settings.NotifyOffline = NotifyOfflineBox.IsChecked == true;
        settings.NotifyTimedOut = NotifyTimedOutBox.IsChecked == true;
        settings.NotifyError = NotifyErrorBox.IsChecked == true;
        AppSettings.Save();
    }

    /// <summary>
    /// Copies the app and CLI versions, for a bug report.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="args">Unused.</param>
    private void CopyVersion_Click(object sender, RoutedEventArgs args)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText($"Wol {Version}, wol.exe {_viewModel?.CliVersion ?? "?"}, {Environment.OSVersion.VersionString}");
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    /// <summary>
    /// Checks the CLI again.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="args">Unused.</param>
    private void CheckCli_Click(object sender, RoutedEventArgs args) => _ = _viewModel?.StartAsync();

    /// <summary>
    /// Opens the folder of the configuration file, with the file selected.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="args">Unused.</param>
    private void OpenConfig_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel?.ConfigPath is not { } path)
        {
            return;
        }

        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // Explorer cannot start: nothing to do.
        }
    }
}
