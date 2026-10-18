using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Wol.Desktop.Services;

namespace Wol.Desktop;

/// <summary>
/// Entry point: one instance per user. Starting Wol again (Start menu, Startup shortcut)
/// hands over to the running instance, which shows its window.
/// </summary>
public static partial class Program
{
    /// <summary>
    /// Key identifying the running instance.
    /// </summary>
    private const string InstanceKey = "wol-gui";

    /// <summary>
    /// Starts the app, or hands over to the instance already running.
    /// </summary>
    /// <param name="args">Command-line arguments; <c>--tray</c> starts in the notification area.</param>
    /// <returns>The exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            Redirect(instance);
            return 0;
        }

        AppSettings.Load();
        if (AppSettings.Current.Language != "system")
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = AppSettings.Current.Language;
        }

        var hidden = args.Contains(StartupShortcut.TrayArgument, StringComparer.OrdinalIgnoreCase)
            || AppSettings.Current.StartMinimized;
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(hidden);
        });
        return 0;
    }

    /// <summary>
    /// Hands this activation over to the running instance, letting it come to the foreground.
    /// </summary>
    /// <param name="instance">The running instance.</param>
    private static void Redirect(AppInstance instance)
    {
        AllowSetForegroundWindow(instance.ProcessId);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // Waiting on the UI-less main thread would block COM: wait on a worker thread.
        Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait();
    }

    /// <summary>
    /// Win32 <c>AllowSetForegroundWindow</c> (user32.dll).
    /// </summary>
    /// <param name="processId">Process allowed to take the foreground.</param>
    /// <returns>True on success.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
