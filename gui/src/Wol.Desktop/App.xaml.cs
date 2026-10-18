using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Wol.Desktop;

/// <summary>
/// Application: one main window, which may live hidden in the notification area. The app ends
/// explicitly (Quit), not when its window closes.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Whether the app starts in the notification area, without showing its window.
    /// </summary>
    private readonly bool _hidden;

    /// <summary>
    /// The main window.
    /// </summary>
    private MainWindow? _window;

    /// <summary>
    /// Builds the application.
    /// </summary>
    /// <param name="hidden">Whether to start in the notification area.</param>
    public App(bool hidden)
    {
        _hidden = hidden;
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    /// <summary>
    /// Creates the main window; shows it unless starting in the notification area.
    /// </summary>
    /// <param name="args">Unused.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;

        // Wol started again: the other instance handed over, show the window.
        AppInstance.GetCurrent().Activated += (_, _) => window.DispatcherQueue.TryEnqueue(window.ShowFromTray);
        if (!_hidden)
        {
            window.ShowFromTray();
        }
    }
}
