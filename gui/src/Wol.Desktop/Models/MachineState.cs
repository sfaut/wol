namespace Wol.Desktop.Models;

/// <summary>
/// State of a machine as shown in the app (see design/specs/states.md).
/// </summary>
public enum MachineState
{
    /// <summary>
    /// Not checked yet, or check running.
    /// </summary>
    Unknown,

    /// <summary>
    /// At least one interface answered.
    /// </summary>
    Online,

    /// <summary>
    /// No interface answered.
    /// </summary>
    Offline,

    /// <summary>
    /// Magic packet sent, waiting for the machine to answer.
    /// </summary>
    Waking,

    /// <summary>
    /// Woken, but it did not answer in time.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The local network cannot be scanned.
    /// </summary>
    Error,
}
