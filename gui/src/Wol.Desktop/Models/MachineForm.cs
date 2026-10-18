using Wol.Desktop.Cli;

namespace Wol.Desktop.Models;

/// <summary>
/// Machine as entered in the Add / Edit dialog, ready to be saved by the CLI.
/// </summary>
/// <param name="Name">Machine name.</param>
/// <param name="Group">Group name; null for no group.</param>
/// <param name="CreateGroup">Whether the group does not exist yet and must be created first.</param>
/// <param name="Interfaces">Network interfaces, MAC addresses normalized.</param>
/// <param name="Broadcast">Broadcast address; null for the default.</param>
/// <param name="Port">UDP port; null for the default.</param>
public sealed record MachineForm(
    string Name,
    string? Group,
    bool CreateGroup,
    IReadOnlyList<InterfaceDto> Interfaces,
    string? Broadcast,
    int? Port);
