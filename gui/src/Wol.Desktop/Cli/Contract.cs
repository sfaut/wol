using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wol.Desktop.Cli;

/// <summary>
/// JSON documents printed by the <c>wol</c> CLI with <c>--json</c> (see docs/cli-contract.md).
/// </summary>
public static class Contract
{
    /// <summary>
    /// Version of the JSON output this app understands.
    /// </summary>
    public const int Schema = 1;

    /// <summary>
    /// Oldest CLI version providing <see cref="Schema"/> and every command this app uses.
    /// </summary>
    public const string MinimumCliVersion = "0.1.0";
}

/// <summary>
/// Configuration file used by a command, and what happened to it.
/// </summary>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="Status"><c>loaded</c>, <c>updated</c>, <c>created</c> or <c>missing</c>.</param>
public sealed record ConfigInfo(string Path, string Status);

/// <summary>
/// Network interface of a machine.
/// </summary>
/// <param name="Mac">MAC address, as <c>AA:BB:CC:DD:EE:FF</c>.</param>
/// <param name="Label">Optional label, e.g. "Wi-Fi".</param>
public sealed record InterfaceDto(string Mac, string? Label);

/// <summary>
/// Machine as stored in the configuration file.
/// </summary>
/// <param name="Name">Unique name.</param>
/// <param name="Group">Group name, if any.</param>
/// <param name="Interfaces">Network interfaces (at least one).</param>
/// <param name="Broadcast">Destination address of the magic packet; default when null.</param>
/// <param name="Port">UDP port of the magic packet; default when null.</param>
public sealed record MachineDto(
    string Name,
    string? Group,
    List<InterfaceDto> Interfaces,
    string? Broadcast,
    int? Port);

/// <summary>
/// Group of machines.
/// </summary>
/// <param name="Name">Unique name.</param>
public sealed record GroupDto(string Name);

/// <summary>
/// Warning about the configuration, e.g. a MAC address shared by several machines.
/// </summary>
/// <param name="Code">Stable code, e.g. <c>shared_mac</c>.</param>
/// <param name="Message">English description.</param>
/// <param name="Mac">MAC address concerned, for <c>shared_mac</c>.</param>
/// <param name="Machines">Machines concerned, for <c>shared_mac</c>.</param>
public sealed record WarningDto(string Code, string Message, string? Mac, List<string>? Machines);

/// <summary>
/// Output of <c>wol list</c>.
/// </summary>
/// <param name="Schema">JSON schema version.</param>
/// <param name="Config">Configuration file used.</param>
/// <param name="Groups">Groups, in display order.</param>
/// <param name="Machines">Machines, in file order.</param>
/// <param name="Warnings">Configuration warnings.</param>
public sealed record ListDocument(
    int Schema,
    ConfigInfo? Config,
    List<GroupDto>? Groups,
    List<MachineDto>? Machines,
    List<WarningDto>? Warnings);

/// <summary>
/// State of a machine, as found by a network scan.
/// </summary>
/// <param name="Machine">Machine name.</param>
/// <param name="State"><c>online</c>, <c>offline</c> or <c>error</c>.</param>
/// <param name="Mac">MAC address of the interface that answered, when online.</param>
/// <param name="Label">Label of the interface that answered, when online.</param>
/// <param name="Address">Current IPv4 address, when online.</param>
/// <param name="Reason">Reason of an error state, e.g. <c>no_local_network</c>.</param>
public sealed record MachineStateDto(
    string Machine,
    string State,
    string? Mac,
    string? Label,
    string? Address,
    string? Reason);

/// <summary>
/// Output of <c>wol status</c>.
/// </summary>
/// <param name="Schema">JSON schema version.</param>
/// <param name="Machines">State of each machine.</param>
public sealed record StatusDocument(int Schema, List<MachineStateDto>? Machines);

/// <summary>
/// Error reported by the CLI.
/// </summary>
/// <param name="Code">Stable code, e.g. <c>config_invalid</c>.</param>
/// <param name="Message">English description.</param>
/// <param name="Details">Structured details, depending on the code.</param>
public sealed record ErrorDto(string Code, string Message, JsonElement Details);

/// <summary>
/// Error document printed on <c>stderr</c>.
/// </summary>
/// <param name="Schema">JSON schema version.</param>
/// <param name="Error">The error.</param>
public sealed record ErrorDocument(int Schema, ErrorDto? Error);

/// <summary>
/// Output of <c>wol version</c>.
/// </summary>
/// <param name="Schema">JSON schema version.</param>
/// <param name="Version">CLI version.</param>
public sealed record VersionDocument(int Schema, string? Version);

/// <summary>
/// NDJSON event of <c>wol watch</c>.
/// </summary>
/// <param name="Event"><c>snapshot</c>, <c>state_changed</c> or <c>error</c>.</param>
/// <param name="Machines">State of every machine, for <c>snapshot</c>.</param>
/// <param name="Machine">New state of a machine, for <c>state_changed</c>.</param>
/// <param name="Error">The error, for <c>error</c>.</param>
public sealed record WatchEvent(
    string Event,
    List<MachineStateDto>? Machines,
    MachineStateDto? Machine,
    ErrorDto? Error);

/// <summary>
/// Result of sending magic packets to one machine.
/// </summary>
/// <param name="Machine">Machine name.</param>
/// <param name="Status"><c>sent</c> or <c>failed</c>.</param>
/// <param name="Error">Why sending failed.</param>
public sealed record WakeResultDto(string Machine, string Status, ErrorDto? Error);

/// <summary>
/// NDJSON event of <c>wol wake --wait</c>.
/// </summary>
/// <param name="Event"><c>sent</c>, <c>online</c>, <c>timed_out</c>, <c>error</c> or <c>finished</c>.</param>
/// <param name="ElapsedS">Seconds since sending, for <c>online</c> and <c>timed_out</c>.</param>
/// <param name="Machine">
///     Machine state object for <c>online</c>, machine name for <c>timed_out</c>.
/// </param>
/// <param name="Results">Send results, for <c>sent</c>.</param>
/// <param name="Error">The error, for <c>error</c>.</param>
public sealed record WakeEvent(
    string Event,
    int? ElapsedS,
    JsonElement Machine,
    List<WakeResultDto>? Results,
    ErrorDto? Error);

/// <summary>
/// Source-generated serialization of the CLI documents (Native AOT, no reflection).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ListDocument))]
[JsonSerializable(typeof(StatusDocument))]
[JsonSerializable(typeof(ErrorDocument))]
[JsonSerializable(typeof(VersionDocument))]
[JsonSerializable(typeof(WatchEvent))]
[JsonSerializable(typeof(WakeEvent))]
[JsonSerializable(typeof(MachineStateDto))]
internal sealed partial class ContractContext : JsonSerializerContext;
