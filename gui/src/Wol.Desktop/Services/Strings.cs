using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Wol.Desktop.Services;

/// <summary>
/// Localized strings from <c>Strings/&lt;locale&gt;/Resources.resw</c>, for text built in code.
/// </summary>
/// <remarks>
/// Keys use <c>_</c> where the design copy uses <c>.</c> (<c>State.Online</c> is
/// <c>State_Online</c>): in a .resw file, a dot separates an <c>x:Uid</c> from a property.
/// </remarks>
internal static class Strings
{
    /// <summary>
    /// Loader of the application resources.
    /// </summary>
    private static readonly ResourceLoader Loader = new();

    /// <summary>
    /// Gets a string.
    /// </summary>
    /// <param name="key">Resource key.</param>
    /// <returns>The localized string, or the key itself when it is missing.</returns>
    public static string Get(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    /// <summary>
    /// Gets a localized string that may not exist.
    /// </summary>
    /// <param name="key">Resource key, with <c>_</c> in place of <c>.</c>.</param>
    /// <returns>The string, or null when there is none.</returns>
    public static string? Find(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Describes an error of the CLI in the user's language, from its stable code; falls back
    /// to the English message of the CLI.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>A sentence for the user.</returns>
    public static string Describe(Cli.ErrorDto error)
    {
        if (Find($"CliError_{error.Code}") is not { } text)
        {
            return error.Message;
        }

        string? Detail(string name) =>
            error.Details.ValueKind == System.Text.Json.JsonValueKind.Object
            && error.Details.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;

        return string.Format(
            CultureInfo.CurrentCulture,
            text,
            Detail("machine") ?? Detail("group") ?? Detail("path") ?? Detail("field") ?? string.Empty);
    }

    /// <summary>
    /// Gets a string and fills its <c>{0}</c>, <c>{1}</c>… placeholders.
    /// </summary>
    /// <param name="key">Resource key.</param>
    /// <param name="args">Placeholder values.</param>
    /// <returns>The formatted, localized string.</returns>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Gets the singular (<c>.One</c>) or plural (<c>.Other</c>) form of a string.
    /// </summary>
    /// <param name="key">Resource key, without the <c>_One</c> / <c>_Other</c> suffix.</param>
    /// <param name="count">Number deciding the form.</param>
    /// <param name="args">Placeholder values.</param>
    /// <returns>The formatted, localized string.</returns>
    public static string Plural(string key, int count, params object?[] args) =>
        Format(count == 1 ? $"{key}_One" : $"{key}_Other", args);
}
