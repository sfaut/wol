using System.Globalization;

namespace Wol.Desktop.Services;

/// <summary>
/// Durations and relative times, as shown to the user.
/// </summary>
internal static class TimeText
{
    /// <summary>
    /// Formats a duration as a clock, e.g. <c>2:05</c> (minutes and seconds).
    /// </summary>
    /// <param name="duration">Duration to format.</param>
    /// <returns>The formatted duration.</returns>
    public static string Clock(TimeSpan duration)
    {
        var seconds = (int)Math.Max(0, duration.TotalSeconds);
        return string.Create(CultureInfo.CurrentCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>
    /// Formats a duration in words, e.g. "1 min 20 s".
    /// </summary>
    /// <param name="duration">Duration to format.</param>
    /// <returns>The formatted duration.</returns>
    public static string Words(TimeSpan duration)
    {
        var seconds = (int)Math.Max(0, duration.TotalSeconds);
        return seconds switch
        {
            < 60 => Strings.Format("Time_Seconds", seconds),
            _ when seconds % 60 == 0 => Strings.Format("Time_Minutes", seconds / 60),
            _ => Strings.Format("Time_MinutesSeconds", seconds / 60, seconds % 60),
        };
    }

    /// <summary>
    /// Formats how long ago something happened, e.g. "5 min ago".
    /// </summary>
    /// <param name="moment">When it happened.</param>
    /// <returns>The relative time.</returns>
    public static string Ago(DateTimeOffset moment)
    {
        var elapsed = DateTimeOffset.Now - moment;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => Strings.Get("Time_JustNow"),
            { TotalHours: < 1 } => Strings.Format("Time_MinutesAgo", (int)elapsed.TotalMinutes),
            { TotalDays: < 1 } => Strings.Format("Time_HoursAgo", (int)elapsed.TotalHours),
            _ => Strings.Format("Time_DaysAgo", (int)elapsed.TotalDays),
        };
    }
}
