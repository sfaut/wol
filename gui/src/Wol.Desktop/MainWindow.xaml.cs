using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Wol.Desktop;

/// <summary>
/// Main application window.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Builds the window and applies its localized title.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // Window is not a FrameworkElement: x:Uid does not apply, the title is localized in code.
        Title = new ResourceLoader().GetString("AppDisplayName");
    }
}
