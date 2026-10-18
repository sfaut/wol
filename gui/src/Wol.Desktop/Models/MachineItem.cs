using Microsoft.UI.Xaml;
using Wol.Desktop.Cli;
using Wol.Desktop.Services;

namespace Wol.Desktop.Models;

/// <summary>
/// A machine row: its definition from <c>wol list</c>, its live state, and the texts shown for
/// them. Call <see cref="ObservableObject.Refresh"/> after changing it.
/// </summary>
public sealed partial class MachineItem : ObservableObject
{
    /// <summary>
    /// Builds a machine whose state is not known yet.
    /// </summary>
    /// <param name="definition">Machine from <c>wol list</c>.</param>
    public MachineItem(MachineDto definition)
    {
        Definition = definition;
    }

    /// <summary>
    /// Machine as stored in the configuration file.
    /// </summary>
    public MachineDto Definition { get; set; }

    /// <summary>
    /// Unique machine name.
    /// </summary>
    public string Name => Definition.Name;

    /// <summary>
    /// Names of the other machines sharing a MAC address with this one.
    /// </summary>
    public IReadOnlyList<string> SharedWith { get; set; } = [];

    /// <summary>
    /// Current state.
    /// </summary>
    public MachineState State { get; set; }

    /// <summary>
    /// State before the current wake run, restored when it is cancelled.
    /// </summary>
    public MachineState StateBeforeWake { get; set; }

    /// <summary>
    /// Current IPv4 address, when online.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Label of the interface that answered, when online.
    /// </summary>
    public string? AnsweringLabel { get; set; }

    /// <summary>
    /// Last time the machine was seen online during this session.
    /// </summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// When the current wake run sent its packets.
    /// </summary>
    public DateTimeOffset WakeStartedAt { get; set; }

    /// <summary>
    /// How long the current or last wake run waits.
    /// </summary>
    public TimeSpan WakeTimeout { get; set; }

    /// <summary>
    /// Wake run the machine is waiting in, if any.
    /// </summary>
    public object? WakeRun { get; set; }

    /// <summary>
    /// Whether a check is running ("Checking…").
    /// </summary>
    public bool IsChecking { get; set; }

    /// <summary>
    /// Whether the companion CLI cannot be used, so the state cannot be known.
    /// </summary>
    public bool CliUnavailable { get; set; }

    /// <summary>
    /// Localized state name.
    /// </summary>
    public string StateLabel => Strings.Get($"State_{State}");

    /// <summary>
    /// Detail shown under the state, e.g. "Last seen 5 min ago".
    /// </summary>
    public string Detail => State switch
    {
        MachineState.Offline when LastSeen is { } seen =>
            Strings.Format("State_Offline_Detail", TimeText.Ago(seen)),
        MachineState.Offline => Strings.Get("State_Offline_Never"),
        MachineState.Waking => Strings.Format(
            "State_Waking_Detail",
            TimeText.Clock(DateTimeOffset.Now - WakeStartedAt)),
        MachineState.TimedOut => Strings.Format("State_TimedOut_Detail", TimeText.Clock(WakeTimeout)),
        MachineState.Unknown when CliUnavailable => Strings.Get("State_Unknown_CliUnavailable"),
        MachineState.Unknown when IsChecking => Strings.Get("State_Unknown_Checking"),
        MachineState.Unknown => Strings.Get("State_Unknown_NotChecked"),
        MachineState.Error => Strings.Get("State_Error_NoNetwork"),
        _ => string.Empty,
    };

    /// <summary>
    /// Second line of the row: interface and IP when online, otherwise the first MAC address.
    /// </summary>
    public string SecondLine
    {
        get
        {
            if (State == MachineState.Online && Address is not null)
            {
                return AnsweringLabel is { } label
                    ? Strings.Format("Row_Online_Line", label, Address)
                    : Address;
            }

            var interfaces = Definition.Interfaces;
            return interfaces.Count switch
            {
                0 => string.Empty,
                1 => interfaces[0].Mac,
                _ => Strings.Format("Row_Mac_More", interfaces[0].Mac, interfaces.Count - 1),
            };
        }
    }

    /// <summary>
    /// Tooltip listing every interface, one per line ("Label · MAC").
    /// </summary>
    public string InterfacesTooltip => string.Join(
        Environment.NewLine,
        Definition.Interfaces.Select(item => item.Label is { } label
            ? Strings.Format("Row_Interfaces_Tooltip_Line", label, item.Mac)
            : item.Mac));

    /// <summary>
    /// Visibility of the shared MAC address warning.
    /// </summary>
    public Visibility SharedMacVisibility =>
        SharedWith.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Tooltip of the shared MAC address warning, naming the other machines.
    /// </summary>
    public string SharedMacTooltip => SharedWith.Count switch
    {
        0 => string.Empty,
        1 => Strings.Format("Row_SharedMac_Tooltip_One", SharedWith[0]),
        _ => Strings.Format("Row_SharedMac_Tooltip_Other", SharedWith[0], SharedWith.Count - 1),
    };

    /// <summary>
    /// Visibility of the accent Wake / Retry button (offline or timed out).
    /// </summary>
    public Visibility AccentWakeVisibility =>
        State is MachineState.Offline or MachineState.TimedOut ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Text of the accent button.
    /// </summary>
    public string AccentWakeText => Strings.Get(State == MachineState.TimedOut ? "Row_Retry" : "Row_Wake");

    /// <summary>
    /// Automation name of the accent button.
    /// </summary>
    public string AccentWakeName =>
        Strings.Format(State == MachineState.TimedOut ? "Row_Retry_Name" : "Row_Wake_Name", Name);

    /// <summary>
    /// Automation name of the More button.
    /// </summary>
    public string MoreName => Strings.Format("Row_More_Name", Name);

    /// <summary>
    /// Visibility of the standard button: Wake when unknown or in error, Cancel while waking.
    /// </summary>
    public Visibility StandardWakeVisibility =>
        State is MachineState.Unknown or MachineState.Error or MachineState.Waking
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Text of the standard button.
    /// </summary>
    public string StandardWakeText => Strings.Get(State == MachineState.Waking ? "Row_Cancel" : "Row_Wake");

    /// <summary>
    /// Automation name of the standard button.
    /// </summary>
    public string StandardWakeName =>
        Strings.Format(State == MachineState.Waking ? "Row_Cancel_Name" : "Row_Wake_Name", Name);

    /// <summary>
    /// Whether the standard button can be used: no packet can be sent without a network.
    /// </summary>
    public bool IsStandardWakeEnabled => State != MachineState.Error && !CliUnavailable;

    /// <summary>
    /// Name read by screen readers: "{name}, {state}, {detail}", plus the shared MAC address.
    /// </summary>
    public string AutomationName
    {
        get
        {
            var name = Strings.Format("Row_AutomationName", Name, StateLabel, Detail);
            return SharedWith.Count > 0
                ? $"{name}, {Strings.Format("Row_SharedMac_Name", string.Join(", ", SharedWith))}"
                : name;
        }
    }

    /// <summary>
    /// Tells whether the machine matches a search: name, MAC addresses, labels or current IP.
    /// </summary>
    /// <param name="text">Text searched for (case-insensitive).</param>
    /// <returns>True when the machine matches, or when the text is empty.</returns>
    public bool Matches(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        bool Has(string? value) => value?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        return Has(Name)
            || Has(Address)
            || Definition.Interfaces.Any(item => Has(item.Mac) || Has(item.Label)
                || Has(item.Mac.Replace(":", string.Empty, StringComparison.Ordinal)));
    }
}
