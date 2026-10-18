using Microsoft.UI.Xaml;

namespace Wol.Desktop;

/// <summary>
/// Application entry point: creates and shows the main window.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Main window, kept alive for the lifetime of the application.
    /// </summary>
    private Window? _window;

    /// <summary>
    /// Initializes the application and its XAML resources.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates and activates the main window when the application is launched.
    /// </summary>
    /// <param name="args">Launch details.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
