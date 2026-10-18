using Microsoft.UI.Xaml.Controls;

namespace Wol.Desktop.Models;

/// <summary>
/// Transient message shown at the bottom of the window (wake results).
/// </summary>
/// <param name="Severity">Severity of the bar.</param>
/// <param name="Title">Title, e.g. "NAS is online".</param>
/// <param name="Message">Message under the title.</param>
/// <param name="ActionText">Text of the action button, if any (e.g. "Retry").</param>
/// <param name="Action">What the action button does.</param>
public sealed record Notification(
    InfoBarSeverity Severity,
    string Title,
    string Message,
    string? ActionText = null,
    Action? Action = null)
{
    /// <summary>
    /// Whether the bar closes by itself after a few seconds (successes only).
    /// </summary>
    public bool AutoClose => Severity == InfoBarSeverity.Success;
}

/// <summary>
/// Kind of event worth a Windows notification (each one can be turned off in Settings).
/// </summary>
public enum AlertKind
{
    /// <summary>
    /// A machine came online, by itself or after a wake-up.
    /// </summary>
    Online,

    /// <summary>
    /// A machine went offline.
    /// </summary>
    Offline,

    /// <summary>
    /// A machine did not wake up in time.
    /// </summary>
    TimedOut,

    /// <summary>
    /// Machines cannot be checked (no local network).
    /// </summary>
    Error,
}

/// <summary>
/// Event worth a Windows notification, shown when the window is not in front.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Title">Title of the notification.</param>
/// <param name="Text">Text of the notification.</param>
public sealed record Alert(AlertKind Kind, string Title, string Text);

/// <summary>
/// Degraded states shown in the bar at the top of the page, by precedence.
/// </summary>
public enum BannerKind
{
    /// <summary>
    /// Nothing to report.
    /// </summary>
    None,

    /// <summary>
    /// <c>wol.exe</c> is not next to <c>wol-gui.exe</c>.
    /// </summary>
    CliMissing,

    /// <summary>
    /// <c>wol.exe</c> does not speak the JSON schema this app understands.
    /// </summary>
    CliIncompatible,

    /// <summary>
    /// The configuration file cannot be read.
    /// </summary>
    ConfigInvalid,

    /// <summary>
    /// The local network cannot be scanned.
    /// </summary>
    NoNetwork,
}

/// <summary>
/// The degraded state currently shown at the top of the page.
/// </summary>
/// <param name="Kind">Which state.</param>
/// <param name="Title">Title of the bar.</param>
/// <param name="Message">Message of the bar.</param>
public sealed record Banner(BannerKind Kind, string Title, string Message);
