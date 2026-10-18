using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Wol.Desktop.Services;

namespace Wol.Desktop.Models;

/// <summary>
/// A section of the machine list: a group, or the machines without a group.
/// </summary>
public sealed partial class GroupSection : ObservableObject
{
    /// <summary>
    /// Builds a section.
    /// </summary>
    /// <param name="group">Group name; null for the machines without a group.</param>
    public GroupSection(string? group)
    {
        Group = group;
    }

    /// <summary>
    /// Group name; null for the machines without a group.
    /// </summary>
    public string? Group { get; }

    /// <summary>
    /// Title of the section.
    /// </summary>
    public string Title => Group ?? Strings.Get("Nav_Ungrouped");

    /// <summary>
    /// Machines of the section, in file order.
    /// </summary>
    public ObservableCollection<MachineItem> Machines { get; } = [];

    /// <summary>
    /// Machines shown, once the search is applied.
    /// </summary>
    public ObservableCollection<MachineItem> Shown { get; } = [];

    /// <summary>
    /// Whether a search is applied.
    /// </summary>
    private bool _searching;

    /// <summary>
    /// Visibility of the section: hidden while searching when none of its machines match.
    /// </summary>
    public Visibility ShownVisibility => _searching && Shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Whether the section is expanded.
    /// </summary>
    public bool IsExpanded { get; set; } = true;

    /// <summary>
    /// Status summary, e.g. "2 online · 1 offline".
    /// </summary>
    public string Summary => Summarize(Machines);

    /// <summary>
    /// Number of machines needing attention: offline or timed out (navigation badge).
    /// </summary>
    public int NotOnlineCount =>
        Machines.Count(machine => machine.State is MachineState.Offline or MachineState.TimedOut);

    /// <summary>
    /// Machines the Wake group button wakes: those not online nor already waking.
    /// </summary>
    public IReadOnlyList<MachineItem> WakeTargets => Machines
        .Where(machine => machine.State is not (MachineState.Online or MachineState.Waking))
        .ToList();

    /// <summary>
    /// Whether the Wake group button can be used.
    /// </summary>
    public bool CanWake => WakeTargets.Any(machine => machine.IsStandardWakeEnabled);

    /// <summary>
    /// Text of the Wake group button.
    /// </summary>
    public string WakeText => Strings.Get("Section_WakeGroup");

    /// <summary>
    /// Shows the machines matching a search.
    /// </summary>
    /// <param name="text">Text searched for; empty shows every machine.</param>
    public void ApplySearch(string text)
    {
        _searching = !string.IsNullOrWhiteSpace(text);
        Shown.Clear();
        foreach (var machine in Machines.Where(machine => machine.Matches(text)))
        {
            Shown.Add(machine);
        }
        OnPropertyChanged(nameof(ShownVisibility));
    }

    /// <summary>
    /// Builds a status summary, e.g. "2 online · 1 offline", in a fixed state order.
    /// </summary>
    /// <param name="machines">Machines to count.</param>
    /// <returns>The summary, or "No machines".</returns>
    public static string Summarize(IEnumerable<MachineItem> machines)
    {
        var counts = machines.GroupBy(machine => machine.State).ToDictionary(group => group.Key, group => group.Count());
        if (counts.Count == 0)
        {
            return Strings.Get("Summary_Empty");
        }

        MachineState[] order =
        [
            MachineState.Online, MachineState.Offline, MachineState.Waking,
            MachineState.TimedOut, MachineState.Unknown, MachineState.Error,
        ];
        var parts = order
            .Where(counts.ContainsKey)
            .Select(state => state == MachineState.Error
                ? Strings.Plural("Summary_Error", counts[state], counts[state])
                : Strings.Format($"Summary_{state}", counts[state]));
        return string.Join(Strings.Get("Summary_Separator"), parts);
    }
}
