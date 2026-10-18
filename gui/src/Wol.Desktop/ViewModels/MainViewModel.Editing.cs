using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Wol.Desktop.Cli;
using Wol.Desktop.Models;
using Wol.Desktop.Services;

namespace Wol.Desktop.ViewModels;

/// <summary>
/// Changes to the configuration: every one is a CLI command (<c>add</c>, <c>edit</c>,
/// <c>remove</c>, <c>group …</c>), followed by a reload of the machine list.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Group names, in display order.
    /// </summary>
    public IReadOnlyList<string> GroupNames =>
        Sections.Where(section => section.Group is not null).Select(section => section.Group!).ToList();

    /// <summary>
    /// Every machine, in no particular order.
    /// </summary>
    public IEnumerable<MachineItem> AllMachines => _machines.Values;

    /// <summary>
    /// Finds a machine by name (case-insensitive).
    /// </summary>
    /// <param name="name">Machine name.</param>
    /// <returns>The machine, or null.</returns>
    public MachineItem? FindMachine(string name) => _machines.GetValueOrDefault(name);

    /// <summary>
    /// Finds the section of a group (case-insensitive); null finds the machines without a group.
    /// </summary>
    /// <param name="group">Group name, or null.</param>
    /// <returns>The section, or null when there is none.</returns>
    public GroupSection? FindSection(string? group) => Sections.FirstOrDefault(section =>
        group is null ? section.Group is null : string.Equals(section.Group, group, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks some machines now (<c>wol status</c>).
    /// </summary>
    /// <param name="machines">Machines to check.</param>
    /// <returns>A task completing once their states are shown.</returns>
    public async Task CheckAsync(IReadOnlyList<MachineItem> machines)
    {
        if (!CanUseCli || machines.Count == 0)
        {
            return;
        }

        try
        {
            var result = await _cli.RunAsync(["status", "--", .. machines.Select(machine => machine.Name)]);
            if (CliClient.Parse(result.Stdout, ContractContext.Default.StatusDocument)?.Machines is { } states)
            {
                Apply(states);
            }
        }
        catch (CliMissingException)
        {
            await StartAsync();
        }
    }

    /// <summary>
    /// Lists the devices on the local network that no machine uses yet (<c>wol scan</c>).
    /// </summary>
    /// <returns>The devices, or the error that prevented the scan.</returns>
    public async Task<(IReadOnlyList<DeviceDto> Devices, ErrorDto? Error)> ScanAsync()
    {
        try
        {
            var result = await _cli.RunAsync(["scan"]);
            if (CliClient.Parse(result.Stdout, ContractContext.Default.ScanDocument)?.Devices is { } devices)
            {
                return (devices.Where(device => device.Machines is not { Count: > 0 }).ToList(), null);
            }
            return ([], result.Error());
        }
        catch (CliMissingException)
        {
            return ([], null);
        }
    }

    /// <summary>
    /// Adds a machine, creating its group first when needed.
    /// </summary>
    /// <param name="form">The machine.</param>
    /// <returns>Null when it was saved, otherwise why it was not.</returns>
    public async Task<string?> AddMachineAsync(MachineForm form)
    {
        if (form is { CreateGroup: true, Group: { } group }
            && await TryChangeAsync(["group", "add", "--", group], reload: false) is { } groupError)
        {
            return groupError;
        }

        List<string> args = ["add"];
        args.AddRange(form.Interfaces.Select(MacArgument));
        if (form.Group is { } name)
        {
            args.Add($"--group={name}");
        }
        if (form.Broadcast is { } broadcast)
        {
            args.Add($"--broadcast={broadcast}");
        }
        if (form.Port is { } port)
        {
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--port={port}"));
        }
        return await TryChangeAsync([.. args, "--", form.Name]);
    }

    /// <summary>
    /// Saves the changes made to a machine, creating its group first when needed.
    /// </summary>
    /// <param name="machine">Machine before the changes.</param>
    /// <param name="form">The machine after the changes.</param>
    /// <returns>Null when it was saved, otherwise why it was not.</returns>
    public async Task<string?> EditMachineAsync(MachineItem machine, MachineForm form)
    {
        if (form is { CreateGroup: true, Group: { } group }
            && await TryChangeAsync(["group", "add", "--", group], reload: false) is { } groupError)
        {
            return groupError;
        }

        List<string> args = ["edit"];
        if (form.Name != machine.Name)
        {
            args.Add($"--name={form.Name}");
        }
        args.AddRange(form.Interfaces.Select(MacArgument));
        args.Add(form.Group is { } name ? $"--group={name}" : "--no-group");
        args.Add(form.Broadcast is { } broadcast ? $"--broadcast={broadcast}" : "--no-broadcast");
        args.Add(form.Port is { } port
            ? string.Create(CultureInfo.InvariantCulture, $"--port={port}")
            : "--no-port");
        return await TryChangeAsync([.. args, "--", machine.Name]);
    }

    /// <summary>
    /// Moves machines to a group.
    /// </summary>
    /// <param name="machines">Machines to move.</param>
    /// <param name="group">Group name; null for no group.</param>
    /// <returns>A task completing once they are moved.</returns>
    public async Task MoveMachinesAsync(IReadOnlyList<MachineItem> machines, string? group)
    {
        foreach (var machine in machines)
        {
            var option = group is null ? "--no-group" : $"--group={group}";
            if (!await ChangeAsync(["edit", option, "--", machine.Name], reload: false))
            {
                break;
            }
        }
        await ReloadAsync();
    }

    /// <summary>
    /// Removes machines from the configuration.
    /// </summary>
    /// <param name="machines">Machines to remove.</param>
    /// <returns>A task completing once they are removed.</returns>
    public async Task RemoveMachinesAsync(IReadOnlyList<MachineItem> machines)
    {
        foreach (var machine in machines)
        {
            if (machine.WakeRun is not null)
            {
                CancelWake(machine);
            }
            if (!await ChangeAsync(["remove", "--", machine.Name], reload: false))
            {
                break;
            }
        }
        await ReloadAsync();
    }

    /// <summary>
    /// Adds a group at the end of the list.
    /// </summary>
    /// <param name="name">Group name.</param>
    /// <returns>Null when it was created, otherwise why it was not.</returns>
    public Task<string?> AddGroupAsync(string name) => TryChangeAsync(["group", "add", "--", name]);

    /// <summary>
    /// Renames a group; its machines follow.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <param name="name">New name.</param>
    /// <returns>Null when it was renamed, otherwise why it was not.</returns>
    public Task<string?> RenameGroupAsync(GroupSection section, string name) =>
        TryChangeAsync(["group", "rename", "--", section.Group!, name]);

    /// <summary>
    /// Deletes a group, moving its machines to Ungrouped or removing them too.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <param name="removeMachines">Whether its machines are removed too.</param>
    /// <returns>A task completing once the group is deleted.</returns>
    public async Task DeleteGroupAsync(GroupSection section, bool removeMachines)
    {
        if (removeMachines)
        {
            foreach (var machine in section.Machines.ToList())
            {
                if (!await ChangeAsync(["remove", "--", machine.Name], reload: false))
                {
                    await ReloadAsync();
                    return;
                }
            }
        }
        await ChangeAsync(["group", "remove", "--ungroup", "--", section.Group!]);
    }

    /// <summary>
    /// Moves a group up or down in the list.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <param name="offset">-1 to move it up, 1 to move it down.</param>
    /// <returns>A task completing once the group is moved.</returns>
    public async Task MoveGroupAsync(GroupSection section, int offset)
    {
        var index = GroupNames.ToList().IndexOf(section.Group!);
        if (index >= 0)
        {
            await MoveGroupToAsync(section, index + 1 + offset);
        }
    }

    /// <summary>
    /// Moves a group to a position in the list (drag and drop); does nothing when it is already there.
    /// </summary>
    /// <param name="section">The group.</param>
    /// <param name="position">New position, starting at 1.</param>
    /// <returns>A task completing once the group is moved.</returns>
    public async Task MoveGroupToAsync(GroupSection section, int position)
    {
        var index = GroupNames.ToList().IndexOf(section.Group!);
        if (index < 0 || position < 1 || position > GroupNames.Count || position == index + 1)
        {
            return;
        }
        await ChangeAsync(["group", "move", "--", section.Group!, position.ToString(CultureInfo.InvariantCulture)]);
    }

    /// <summary>
    /// Builds the <c>--mac</c> option of an interface: <c>--mac=MAC</c> or <c>--mac=MAC=label</c>.
    /// </summary>
    /// <param name="item">The interface.</param>
    /// <returns>The option.</returns>
    private static string MacArgument(InterfaceDto item) =>
        item.Label is { } label ? $"--mac={item.Mac}={label}" : $"--mac={item.Mac}";

    /// <summary>
    /// Runs a command changing the configuration; shows its error in a notification, if any.
    /// </summary>
    /// <param name="args">Command and options.</param>
    /// <param name="reload">Whether to reload the machines after a success.</param>
    /// <returns>True when the command succeeded.</returns>
    private async Task<bool> ChangeAsync(IReadOnlyList<string> args, bool reload = true)
    {
        if (await TryChangeAsync(args, reload) is not { } error)
        {
            return true;
        }

        Notify(new Notification(InfoBarSeverity.Error, Strings.Get("Change_Failed_Title"), error));
        return false;
    }

    /// <summary>
    /// Runs a command changing the configuration.
    /// </summary>
    /// <param name="args">Command and options.</param>
    /// <param name="reload">Whether to reload the machines after a success.</param>
    /// <returns>Null when the command succeeded, otherwise its error for the user.</returns>
    private async Task<string?> TryChangeAsync(IReadOnlyList<string> args, bool reload = true)
    {
        if (!CanUseCli)
        {
            return Banner?.Message ?? Strings.Get("Error_CliMissing_Title");
        }

        CliResult result;
        try
        {
            result = await _cli.RunAsync(args);
        }
        catch (CliMissingException)
        {
            _cliMissing = true;
            UpdateBanner();
            return Banner?.Message;
        }

        if (result.ExitCode != 0)
        {
            var error = result.Error();
            if (error?.Code == "config_invalid")
            {
                HandleError(error);
            }
            return error is null ? result.Stderr.Trim() : Strings.Describe(error);
        }

        if (reload)
        {
            await ReloadAsync();
        }
        return null;
    }
}