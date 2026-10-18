using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wol.Desktop.Services;

/// <summary>
/// Preferences of the GUI, saved as <c>wol-gui.json</c> in the folder of the CLI configuration
/// file: next to the executables in portable mode (a <c>wol.toml</c> or <c>wol-gui.json</c>
/// there), otherwise <c>%APPDATA%\wol\</c>. Nothing is written to the registry.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// File name of the settings.
    /// </summary>
    public const string FileName = "wol-gui.json";

    /// <summary>
    /// Smallest and largest check interval, in seconds (as accepted by <c>wol watch</c>).
    /// </summary>
    public const int MinCheckInterval = 1, MaxCheckInterval = 3600;

    /// <summary>
    /// Smallest and largest wake timeout, in seconds.
    /// </summary>
    public const int MinWakeTimeout = 30, MaxWakeTimeout = 900;

    /// <summary>
    /// App theme: <c>system</c>, <c>light</c> or <c>dark</c>.
    /// </summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Display language: <c>system</c>, <c>en-US</c> or <c>fr-FR</c> (applies on restart).
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// Whether the app opens to the notification area instead of showing its window.
    /// </summary>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Whether closing the window keeps the app running in the notification area.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Time between two checks of <c>wol watch</c>, in seconds.
    /// </summary>
    public int CheckInterval { get; set; } = 10;

    /// <summary>
    /// How long a wake-up waits for the machines, in seconds.
    /// </summary>
    public int WakeTimeout { get; set; } = 120;

    /// <summary>
    /// Master switch of the notifications shown from the notification area icon.
    /// </summary>
    public bool Notify { get; set; } = true;

    /// <summary>
    /// Notify when a machine comes online.
    /// </summary>
    public bool NotifyOnline { get; set; } = true;

    /// <summary>
    /// Notify when a machine goes offline.
    /// </summary>
    public bool NotifyOffline { get; set; }

    /// <summary>
    /// Notify when a machine does not wake up.
    /// </summary>
    public bool NotifyTimedOut { get; set; } = true;

    /// <summary>
    /// Notify when machines cannot be checked (no local network).
    /// </summary>
    public bool NotifyError { get; set; } = true;

    /// <summary>
    /// Whether the "still running" notification was already shown, the first time the window
    /// was closed to the notification area.
    /// </summary>
    public bool StillRunningShown { get; set; }

    /// <summary>
    /// Settings of this run, loaded at startup.
    /// </summary>
    [JsonIgnore]
    public static AppSettings Current { get; private set; } = new();

    /// <summary>
    /// Path of the settings file.
    /// </summary>
    [JsonIgnore]
    public static string Path { get; } = FindPath();

    /// <summary>
    /// Loads the settings; defaults when the file is missing or unreadable.
    /// </summary>
    public static void Load()
    {
        try
        {
            Current = File.Exists(Path)
                ? JsonSerializer.Deserialize(File.ReadAllText(Path), SettingsContext.Default.AppSettings) ?? new()
                : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Current = new();
        }

        Current.CheckInterval = Math.Clamp(Current.CheckInterval, MinCheckInterval, MaxCheckInterval);
        Current.WakeTimeout = Math.Clamp(Current.WakeTimeout, MinWakeTimeout, MaxWakeTimeout);
    }

    /// <summary>
    /// Saves the settings; a failure is ignored (the settings still apply to this run).
    /// </summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(Current, SettingsContext.Default.AppSettings));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Read-only folder: the settings last for this run only.
        }
    }

    /// <summary>
    /// Finds where the settings live, following the lookup of the CLI configuration file.
    /// </summary>
    /// <returns>The path of the settings file.</returns>
    private static string FindPath()
    {
        var portable = AppFolder.Root;
        if (File.Exists(System.IO.Path.Combine(portable, FileName)) || File.Exists(System.IO.Path.Combine(portable, "wol.toml")))
        {
            return System.IO.Path.Combine(portable, FileName);
        }

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "wol",
            FileName);
    }
}

/// <summary>
/// Source-generated serialization of the settings (Native AOT, no reflection).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsContext : JsonSerializerContext;
