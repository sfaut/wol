using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Wol.Desktop.Services;

namespace Wol.Desktop.Cli;

/// <summary>
/// Output of a finished CLI process.
/// </summary>
/// <param name="ExitCode">Process exit code (see docs/cli-contract.md).</param>
/// <param name="Stdout">Standard output (empty for streamed commands).</param>
/// <param name="Stderr">Standard error, holding the JSON error document on failure.</param>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>
    /// Parses the error document printed on <c>stderr</c>, if any.
    /// </summary>
    /// <returns>The error, or null when <c>stderr</c> holds no JSON error.</returns>
    public ErrorDto? Error() => CliClient.Parse(Stderr, ContractContext.Default.ErrorDocument)?.Error;
}

/// <summary>
/// Thrown when the CLI executable cannot be started.
/// </summary>
/// <param name="path">Expected path of the executable.</param>
/// <param name="inner">Why it could not be started.</param>
public sealed class CliMissingException(string path, Exception inner)
    : Exception($"Cannot start {path}", inner);

/// <summary>
/// Runs the companion <c>wol.exe</c>, which sits at the root of the app (next to the
/// <c>wol-gui.exe</c> launcher), always with <c>--json</c>.
/// </summary>
public sealed class CliClient
{
    /// <summary>
    /// File name of the CLI executable.
    /// </summary>
    public const string FileName = "wol.exe";

    /// <summary>
    /// Full path of the CLI executable.
    /// </summary>
    public string Path { get; } = System.IO.Path.Combine(AppFolder.Root, FileName);

    /// <summary>
    /// Folder expected to hold the CLI executable.
    /// </summary>
    public string Folder => AppFolder.Root;

    /// <summary>
    /// Tells whether the CLI executable exists.
    /// </summary>
    public bool Exists => File.Exists(Path);

    /// <summary>
    /// Runs a command to completion.
    /// </summary>
    /// <param name="args">Command and options, without <c>--json</c>.</param>
    /// <param name="token">Kills the process when cancelled.</param>
    /// <returns>The exit code and both outputs.</returns>
    /// <exception cref="CliMissingException">The executable cannot be started.</exception>
    public async Task<CliResult> RunAsync(IEnumerable<string> args, CancellationToken token = default)
    {
        using var process = Start(args);
        using var registration = token.Register(() => Kill(process));
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Runs a streaming command (NDJSON on <c>stdout</c>), handing each line over as it comes.
    /// </summary>
    /// <param name="args">Command and options, without <c>--json</c>.</param>
    /// <param name="onLine">
    ///     Called for every line, in the caller's synchronization context (the UI thread when
    ///     called from it).
    /// </param>
    /// <param name="token">Kills the process when cancelled.</param>
    /// <returns>The exit code and <c>stderr</c>.</returns>
    /// <exception cref="CliMissingException">The executable cannot be started.</exception>
    public async Task<CliResult> StreamAsync(
        IEnumerable<string> args,
        Func<string, Task> onLine,
        CancellationToken token)
    {
        using var process = Start(args);
        using var registration = token.Register(() => Kill(process));
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (!token.IsCancellationRequested && line.Length > 0)
            {
                await onLine(line);
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        return new CliResult(process.ExitCode, string.Empty, await stderr);
    }

    /// <summary>
    /// Parses a JSON document, tolerating anything that is not one.
    /// </summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="json">Text to parse.</param>
    /// <param name="type">Source-generated type information.</param>
    /// <returns>The document, or null when the text is not a valid document.</returns>
    public static T? Parse<T>(string json, JsonTypeInfo<T> type)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the CLI without a console window, with redirected UTF-8 outputs.
    /// </summary>
    /// <param name="args">Command and options, without <c>--json</c>.</param>
    /// <returns>The started process.</returns>
    /// <exception cref="CliMissingException">The executable cannot be started.</exception>
    private Process Start(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Global option first: names after a "--" separator may start with a dash.
        info.ArgumentList.Add("--json");
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            return Process.Start(info) ?? throw new InvalidOperationException("No process started");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            throw new CliMissingException(Path, error);
        }
    }

    /// <summary>
    /// Kills a process, ignoring a process that has already exited.
    /// </summary>
    /// <param name="process">Process to kill.</param>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
