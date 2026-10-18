namespace Wol.Desktop.Services;

/// <summary>
/// Folders of the app. A release keeps the executables at its root (the <c>wol-gui.exe</c>
/// launcher and <c>wol.exe</c>, plus <c>wol.toml</c> in portable mode) and the GUI with the
/// Windows App SDK in a <c>runtime</c> subfolder; a development build has everything in one folder.
/// </summary>
internal static class AppFolder
{
    /// <summary>
    /// Name of the subfolder holding the GUI in a release.
    /// </summary>
    public const string RuntimeName = "runtime";

    /// <summary>
    /// Folder of the GUI executable and its resources (<c>Assets</c>, <c>.pri</c> files).
    /// </summary>
    public static string Runtime { get; } = AppContext.BaseDirectory;

    /// <summary>
    /// Root of the app: the parent of the <c>runtime</c> subfolder in a release, the folder of
    /// the GUI executable otherwise. It holds <c>wol.exe</c> and the portable configuration.
    /// </summary>
    public static string Root { get; } = FindRoot();

    /// <summary>
    /// What starts Wol: the launcher at the root of a release, the GUI executable otherwise.
    /// </summary>
    public static string Launcher { get; } = FindLauncher();

    /// <summary>
    /// Finds the root of the app.
    /// </summary>
    /// <returns>The root folder, with a trailing separator.</returns>
    private static string FindRoot()
    {
        var runtime = new DirectoryInfo(Runtime);
        return string.Equals(runtime.Name, RuntimeName, StringComparison.OrdinalIgnoreCase) && runtime.Parent is { } parent
            ? Path.TrimEndingDirectorySeparator(parent.FullName) + Path.DirectorySeparatorChar
            : Runtime;
    }

    /// <summary>
    /// Finds what starts Wol.
    /// </summary>
    /// <returns>The path of the launcher when there is one, of the GUI executable otherwise.</returns>
    private static string FindLauncher()
    {
        var launcher = Path.Combine(Root, Path.GetFileName(Environment.ProcessPath!));
        return Root != Runtime && File.Exists(launcher) ? launcher : Environment.ProcessPath!;
    }
}
