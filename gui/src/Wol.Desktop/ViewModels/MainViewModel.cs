using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Wol.Desktop.Cli;
using Wol.Desktop.Models;
using Wol.Desktop.Services;

namespace Wol.Desktop.ViewModels;

/// <summary>
/// State of the whole app, fed by the CLI: machine definitions (<c>wol list</c>), live states
/// (one long-lived <c>wol watch</c>), checks (<c>wol status</c>) and wake runs
/// (<c>wol wake --wait</c>). All members are used from the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>
    /// Time between two scans of <c>wol watch</c>, in seconds.
    /// </summary>
    public const int CheckInterval = 10;

    /// <summary>
    /// How long a wake run waits for the machines to come online.
    /// </summary>
    public static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Delay before starting <c>wol watch</c> again after it stopped.
    /// </summary>
    private static readonly TimeSpan WatchRestartDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs the companion CLI.
    /// </summary>
    private readonly CliClient _cli = new();

    /// <summary>
    /// Machines by name (names are case-insensitive), kept across reloads to keep their state.
    /// </summary>
    private readonly Dictionary<string, MachineItem> _machines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refreshes the elapsed times of waking machines and the relative "last seen" times.
    /// </summary>
    private readonly DispatcherQueueTimer _clock;

    /// <summary>
    /// Stops the current <c>wol watch</c> process.
    /// </summary>
    private CancellationTokenSource? _watch;

    /// <summary>
    /// Number of clock ticks, to refresh relative times less often than elapsed times.
    /// </summary>
    private int _ticks;

    /// <summary>
    /// Text searched for, applied again whenever the sections are rebuilt.
    /// </summary>
    private string _search = string.Empty;

    /// <summary>
    /// Whether the companion CLI is missing.
    /// </summary>
    private bool _cliMissing;

    /// <summary>
    /// CLI version found when it is incompatible, null otherwise.
    /// </summary>
    private string? _cliIncompatible;

    /// <summary>
    /// Error reading the configuration file, if any.
    /// </summary>
    private ErrorDto? _configError;

    /// <summary>
    /// Builds the view model; call <see cref="StartAsync"/> once the window is shown.
    /// </summary>
    /// <param name="dispatcher">Dispatcher of the UI thread.</param>
    public MainViewModel(DispatcherQueue dispatcher)
    {
        _clock = dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
    }

    /// <summary>
    /// Raised when the group list changed (navigation items to rebuild).
    /// </summary>
    public event EventHandler? GroupsChanged;

    /// <summary>
    /// Raised when machine states changed (summaries and badges to update).
    /// </summary>
    public event EventHandler? StatesChanged;

    /// <summary>
    /// Raised to show a transient message at the bottom of the window.
    /// </summary>
    public event EventHandler<Notification>? Notified;

    /// <summary>
    /// Sections of the machine list: groups in their order, then the machines without a group.
    /// </summary>
    public ObservableCollection<GroupSection> Sections { get; } = [];

    /// <summary>
    /// Degraded state to show at the top of the page, if any.
    /// </summary>
    public Banner? Banner { get; private set; }

    /// <summary>
    /// Whether at least one machine is configured.
    /// </summary>
    public bool HasMachines => _machines.Count > 0;

    /// <summary>
    /// Whether checks and wake-ups are possible.
    /// </summary>
    public bool CanUseCli => !_cliMissing && _cliIncompatible is null;

    /// <summary>
    /// Checks the CLI, loads the machines and starts monitoring them. Safe to call again
    /// ("Check again", "Reload").
    /// </summary>
    /// <returns>A task completing once the machines are loaded.</returns>
    public async Task StartAsync()
    {
        StopWatching();
        _cliMissing = false;
        _cliIncompatible = null;

        if (!await CheckCliAsync())
        {
            foreach (var machine in _machines.Values)
            {
                machine.CliUnavailable = true;
                machine.Refresh();
            }
            UpdateBanner();
            return;
        }

        await ReloadAsync();
        StartWatching();
    }

    /// <summary>
    /// Checks every machine now, with one scan (<c>wol status</c>).
    /// </summary>
    /// <returns>A task completing once the states are shown.</returns>
    public async Task RefreshAsync()
    {
        if (!CanUseCli)
        {
            return;
        }

        foreach (var machine in _machines.Values.Where(machine => machine.State == MachineState.Unknown))
        {
            machine.IsChecking = true;
            machine.Refresh();
        }

        try
        {
            var result = await _cli.RunAsync(["status"]);
            var document = CliClient.Parse(result.Stdout, ContractContext.Default.StatusDocument);
            if (document?.Machines is { } states)
            {
                Apply(states);
            }
            else if (result.Error() is { } error)
            {
                HandleError(error);
            }
        }
        catch (CliMissingException)
        {
            await StartAsync();
        }
    }

    /// <summary>
    /// Wakes machines and waits for them (<c>wol wake --wait</c>), streaming their progress.
    /// </summary>
    /// <param name="targets">Machines to wake; those already waking are skipped.</param>
    /// <param name="title">Name of the group or selection, for several machines.</param>
    /// <returns>A task completing when the run is over.</returns>
    public async Task WakeAsync(IReadOnlyList<MachineItem> targets, string? title = null)
    {
        var machines = targets.Where(machine => machine.State != MachineState.Waking).ToList();
        if (machines.Count == 0 || !CanUseCli)
        {
            return;
        }

        var run = new WakeRun(machines, title ?? machines[0].Name);
        foreach (var machine in machines)
        {
            machine.StateBeforeWake = machine.State;
            machine.State = MachineState.Waking;
            machine.WakeStartedAt = DateTimeOffset.Now;
            machine.WakeTimeout = WakeTimeout;
            machine.WakeRun = run;
            machine.Refresh();
        }
        OnStatesChanged();

        List<string> args = ["wake", .. machines.Select(machine => machine.Name)];
        args.Add(string.Create(CultureInfo.InvariantCulture, $"--wait={(int)WakeTimeout.TotalSeconds}"));
        try
        {
            var result = await _cli.StreamAsync(args, line => OnWakeEvent(run, line), run.Cancellation.Token);
            if (!run.Cancellation.IsCancellationRequested && result.Error() is { } error)
            {
                Notify(new Notification(
                    InfoBarSeverity.Error,
                    Strings.Get("Wake_SendFailed_Title"),
                    Strings.Format("Wake_SendFailed_Message", error.Message)));
            }
        }
        catch (CliMissingException)
        {
            _cliMissing = true;
            UpdateBanner();
        }

        // Cancelled, or the process ended early: machines still waiting go back to their state.
        foreach (var machine in machines.Where(machine => machine.WakeRun == run))
        {
            EndWake(machine, machine.StateBeforeWake);
        }
        OnStatesChanged();
    }

    /// <summary>
    /// Stops waiting for a machine (the packet is already sent).
    /// </summary>
    /// <param name="machine">Machine waking.</param>
    public void CancelWake(MachineItem machine)
    {
        if (machine.WakeRun is not WakeRun run)
        {
            return;
        }

        EndWake(machine, machine.StateBeforeWake);
        if (run.Machines.All(other => other.WakeRun != run))
        {
            run.Cancellation.Cancel();
        }
        OnStatesChanged();
    }

    /// <summary>
    /// Shows the machines matching a search in every section.
    /// </summary>
    /// <param name="text">Text searched for.</param>
    public void ApplySearch(string text)
    {
        _search = text;
        foreach (var section in Sections)
        {
            section.ApplySearch(text);
        }
    }

    /// <summary>
    /// Stops the background processes, before the app exits.
    /// </summary>
    public void Shutdown()
    {
        StopWatching();
        _clock.Stop();
    }

    /// <summary>
    /// Checks that the CLI exists and speaks the expected JSON schema (<c>wol version</c>).
    /// </summary>
    /// <returns>True when it can be used.</returns>
    private async Task<bool> CheckCliAsync()
    {
        try
        {
            var result = await _cli.RunAsync(["version"]);
            var version = CliClient.Parse(result.Stdout, ContractContext.Default.VersionDocument);
            if (version?.Schema == Contract.Schema)
            {
                return true;
            }

            _cliIncompatible = version?.Version ?? Strings.Get("Error_CliIncompatible_Older");
        }
        catch (CliMissingException)
        {
            _cliMissing = true;
        }

        return false;
    }

    /// <summary>
    /// Loads the machine definitions (<c>wol list</c>), keeping the state of known machines.
    /// </summary>
    /// <returns>A task completing once the sections are rebuilt.</returns>
    private async Task ReloadAsync()
    {
        CliResult result;
        try
        {
            result = await _cli.RunAsync(["list"]);
        }
        catch (CliMissingException)
        {
            _cliMissing = true;
            UpdateBanner();
            return;
        }

        var document = CliClient.Parse(result.Stdout, ContractContext.Default.ListDocument);
        if (document is null)
        {
            HandleError(result.Error());
            return;
        }

        _configError = null;
        var definitions = document.Machines ?? [];
        var shared = SharedMacs(document.Warnings ?? []);

        var known = new Dictionary<string, MachineItem>(_machines, StringComparer.OrdinalIgnoreCase);
        _machines.Clear();
        foreach (var definition in definitions)
        {
            var machine = known.TryGetValue(definition.Name, out var existing)
                ? existing
                : new MachineItem(definition);
            machine.Definition = definition;
            machine.SharedWith = shared.TryGetValue(definition.Name, out var others) ? others : [];
            machine.CliUnavailable = false;
            machine.Refresh();
            _machines[definition.Name] = machine;
        }

        var expanded = Sections.ToDictionary(section => section.Group ?? string.Empty, section => section.IsExpanded);
        Sections.Clear();
        var groups = (document.Groups ?? []).Select(group => (string?)group.Name);
        if (definitions.Any(definition => definition.Group is null))
        {
            groups = groups.Append(null);
        }
        foreach (var group in groups)
        {
            var section = new GroupSection(group)
            {
                IsExpanded = expanded.GetValueOrDefault(group ?? string.Empty, true),
            };
            foreach (var definition in definitions.Where(definition => definition.Group == group))
            {
                section.Machines.Add(_machines[definition.Name]);
            }
            section.ApplySearch(_search);
            Sections.Add(section);
        }

        UpdateBanner();
        OnPropertyChanged(nameof(HasMachines));
        GroupsChanged?.Invoke(this, EventArgs.Empty);
        OnStatesChanged();
    }

    /// <summary>
    /// Lists, for every machine sharing a MAC address, the other machines using it.
    /// </summary>
    /// <param name="warnings">Warnings of <c>wol list</c>.</param>
    /// <returns>Other machines, by machine name.</returns>
    private static Dictionary<string, IReadOnlyList<string>> SharedMacs(IEnumerable<WarningDto> warnings)
    {
        var shared = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var names in warnings.Where(warning => warning.Code == "shared_mac").Select(warning => warning.Machines ?? []))
        {
            foreach (var name in names)
            {
                if (!shared.TryGetValue(name, out var others))
                {
                    shared[name] = others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                others.UnionWith(names.Where(other => !string.Equals(other, name, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return shared.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)[.. entry.Value],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts the long-lived <c>wol watch</c> process, restarting it whenever it stops.
    /// </summary>
    private void StartWatching()
    {
        var watch = new CancellationTokenSource();
        _watch = watch;
        _ = WatchAsync(watch.Token);
    }

    /// <summary>
    /// Stops the <c>wol watch</c> process.
    /// </summary>
    private void StopWatching()
    {
        _watch?.Cancel();
        _watch = null;
    }

    /// <summary>
    /// Runs <c>wol watch</c> until cancelled, applying its events.
    /// </summary>
    /// <param name="token">Stops monitoring.</param>
    /// <returns>A task completing when monitoring stops.</returns>
    private async Task WatchAsync(CancellationToken token)
    {
        var snapshots = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var interval = CheckInterval.ToString(CultureInfo.InvariantCulture);
                var result = await _cli.StreamAsync(
                    ["watch", "--interval", interval],
                    async line =>
                    {
                        // After the first snapshot, a snapshot means the configuration changed.
                        if (await OnWatchEvent(line, snapshots > 0))
                        {
                            snapshots++;
                        }
                    },
                    token);
                if (!token.IsCancellationRequested && result.Error() is { } error)
                {
                    HandleError(error);
                }
            }
            catch (CliMissingException)
            {
                _cliMissing = true;
                UpdateBanner();
                return;
            }

            try
            {
                await Task.Delay(WatchRestartDelay, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            snapshots = 0;
        }
    }

    /// <summary>
    /// Applies one <c>wol watch</c> event.
    /// </summary>
    /// <param name="line">NDJSON line.</param>
    /// <param name="reload">Whether a snapshot means the configuration changed.</param>
    /// <returns>True when the event was a snapshot.</returns>
    private async Task<bool> OnWatchEvent(string line, bool reload)
    {
        var watchEvent = CliClient.Parse(line, ContractContext.Default.WatchEvent);
        switch (watchEvent)
        {
            case { Event: "snapshot", Machines: var states }:
                if (reload || _configError is not null)
                {
                    await ReloadAsync();
                }
                Apply(states ?? []);
                return true;
            case { Event: "state_changed", Machine: { } state }:
                Apply([state]);
                break;
            case { Event: "error", Error: { } error }:
                HandleError(error);
                break;
        }
        return false;
    }

    /// <summary>
    /// Applies one <c>wol wake --wait</c> event.
    /// </summary>
    /// <param name="run">Wake run the event belongs to.</param>
    /// <param name="line">NDJSON line.</param>
    /// <returns>A completed task.</returns>
    private Task OnWakeEvent(WakeRun run, string line)
    {
        var wakeEvent = CliClient.Parse(line, ContractContext.Default.WakeEvent);
        switch (wakeEvent?.Event)
        {
            case "sent":
                foreach (var failed in (wakeEvent.Results ?? []).Where(result => result.Status != "sent"))
                {
                    if (Find(run, failed.Machine) is { } machine)
                    {
                        EndWake(machine, machine.StateBeforeWake);
                    }
                    Notify(new Notification(
                        InfoBarSeverity.Error,
                        Strings.Get("Wake_SendFailed_Title"),
                        Strings.Format("Wake_SendFailed_Message", failed.Error?.Message ?? failed.Machine)));
                }
                break;
            case "online" when wakeEvent.Machine.ValueKind == JsonValueKind.Object:
                var state = wakeEvent.Machine.Deserialize(ContractContext.Default.MachineStateDto);
                if (state is not null && Find(run, state.Machine) is { } woken)
                {
                    EndWake(woken, MachineState.Online);
                    ApplyOnline(woken, state);
                    run.Online.Add(woken);
                    if (run.Machines.Count == 1)
                    {
                        Notify(new Notification(
                            InfoBarSeverity.Success,
                            Strings.Format("Wake_Success_Title", woken.Name),
                            Strings.Format("Wake_Success_Message", TimeText.Words(TimeSpan.FromSeconds(wakeEvent.ElapsedS ?? 0)))));
                    }
                }
                break;
            case "timed_out" when wakeEvent.Machine.ValueKind == JsonValueKind.String:
                if (Find(run, wakeEvent.Machine.GetString()) is { } late)
                {
                    EndWake(late, MachineState.TimedOut);
                    run.TimedOut.Add(late);
                    if (run.Machines.Count == 1)
                    {
                        Notify(new Notification(
                            InfoBarSeverity.Warning,
                            Strings.Format("Wake_TimedOut_Title", late.Name),
                            Strings.Format("Wake_TimedOut_Message", TimeText.Words(WakeTimeout)),
                            Strings.Get("Wake_Retry"),
                            () => _ = WakeAsync([late])));
                    }
                }
                break;
            case "error" when wakeEvent.Error is { } error:
                HandleError(error);
                break;
            case "finished" when run.Machines.Count > 1:
                NotifyRunResult(run);
                break;
        }

        OnStatesChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the result of a wake run of several machines.
    /// </summary>
    /// <param name="run">The finished run.</param>
    private void NotifyRunResult(WakeRun run)
    {
        var total = run.Online.Count + run.TimedOut.Count;
        if (total == 0)
        {
            return;
        }

        if (run.TimedOut.Count == 0)
        {
            Notify(new Notification(
                InfoBarSeverity.Success,
                Strings.Format("Wake_Result_AllOnline_Title", run.Title, total),
                Strings.Format("Wake_Result_AllOnline_Message", TimeText.Words(DateTimeOffset.Now - run.StartedAt))));
            return;
        }

        var late = run.TimedOut.ToList();
        Notify(new Notification(
            InfoBarSeverity.Warning,
            Strings.Format("Wake_Result_Partial_Title", run.Title, run.Online.Count, total),
            late.Count == 1
                ? Strings.Format("Wake_Result_Partial_Message_One", late[0].Name)
                : Strings.Format("Wake_Result_Partial_Message_Other", late.Count),
            Strings.Plural("Wake_Result_Retry", late.Count, late.Count),
            () => _ = WakeAsync(late, run.Title)));
    }

    /// <summary>
    /// Finds a machine of a wake run still waiting in it.
    /// </summary>
    /// <param name="run">Wake run.</param>
    /// <param name="name">Machine name.</param>
    /// <returns>The machine, or null when it is no longer waiting in this run.</returns>
    private static MachineItem? Find(WakeRun run, string? name) => run.Machines.FirstOrDefault(machine =>
        machine.WakeRun == run && string.Equals(machine.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Takes a machine out of its wake run.
    /// </summary>
    /// <param name="machine">Machine waking.</param>
    /// <param name="state">State it ends in.</param>
    private static void EndWake(MachineItem machine, MachineState state)
    {
        machine.WakeRun = null;
        machine.State = state;
        machine.Refresh();
    }

    /// <summary>
    /// Applies states found by a scan; machines waking are left to their wake run.
    /// </summary>
    /// <param name="states">States from <c>wol status</c> or <c>wol watch</c>.</param>
    private void Apply(IEnumerable<MachineStateDto> states)
    {
        foreach (var state in states)
        {
            if (!_machines.TryGetValue(state.Machine, out var machine) || machine.State == MachineState.Waking)
            {
                continue;
            }

            machine.IsChecking = false;
            switch (state.State)
            {
                case "online":
                    ApplyOnline(machine, state);
                    break;
                case "offline":
                    // A timed-out machine stays timed out until it answers or is woken again.
                    if (machine.State != MachineState.TimedOut)
                    {
                        machine.State = MachineState.Offline;
                    }
                    machine.Address = null;
                    machine.AnsweringLabel = null;
                    break;
                default:
                    machine.State = MachineState.Error;
                    machine.Address = null;
                    break;
            }
            machine.Refresh();
        }

        UpdateBanner();
        OnStatesChanged();
    }

    /// <summary>
    /// Shows a machine online, with the interface that answered.
    /// </summary>
    /// <param name="machine">Machine.</param>
    /// <param name="state">Its online state.</param>
    private static void ApplyOnline(MachineItem machine, MachineStateDto state)
    {
        machine.State = MachineState.Online;
        machine.Address = state.Address;
        machine.AnsweringLabel = state.Label;
        machine.LastSeen = DateTimeOffset.Now;
        machine.Refresh();
    }

    /// <summary>
    /// Shows an error of the CLI: in the banner for the configuration and the network.
    /// </summary>
    /// <param name="error">The error, if any.</param>
    private void HandleError(ErrorDto? error)
    {
        switch (error?.Code)
        {
            case "config_invalid":
                _configError = error;
                break;
            case "no_local_network" or "scan_failed":
                foreach (var machine in _machines.Values.Where(machine => machine.State != MachineState.Waking))
                {
                    machine.State = MachineState.Error;
                    machine.Refresh();
                }
                OnStatesChanged();
                break;
        }
        UpdateBanner();
    }

    /// <summary>
    /// Picks the banner to show, by precedence: CLI missing, CLI incompatible, configuration
    /// invalid, no local network.
    /// </summary>
    private void UpdateBanner()
    {
        Banner = true switch
        {
            _ when _cliMissing => new Banner(
                BannerKind.CliMissing,
                Strings.Get("Error_CliMissing_Title"),
                Strings.Format("Error_CliMissing_Message", _cli.Folder)),
            _ when _cliIncompatible is { } found => new Banner(
                BannerKind.CliIncompatible,
                Strings.Get("Error_CliIncompatible_Title"),
                Strings.Format("Error_CliIncompatible_Message", Contract.MinimumCliVersion, found)),
            _ when _configError is { } error => new Banner(
                BannerKind.ConfigInvalid,
                Strings.Get("Error_Config_Title"),
                ConfigErrorLine(error) is { } line
                    ? Strings.Format("Error_Config_MessageWithLine", line, error.Message)
                    : Strings.Format("Error_Config_Message", error.Message)),
            _ when _machines.Values.Any(machine => machine.State == MachineState.Error) => new Banner(
                BannerKind.NoNetwork,
                Strings.Get("Error_NoNetwork_Title"),
                Strings.Get("Error_NoNetwork_Message")),
            _ => null,
        };
        OnPropertyChanged(nameof(Banner));
        OnPropertyChanged(nameof(CanUseCli));
    }

    /// <summary>
    /// Path of the configuration file named by a <c>config_invalid</c> error.
    /// </summary>
    public string? ConfigErrorPath => _configError?.Details is { ValueKind: JsonValueKind.Object } details
        && details.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString()
            : null;

    /// <summary>
    /// Line of a <c>config_invalid</c> error, when known.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>The 1-based line, or null.</returns>
    private static int? ConfigErrorLine(ErrorDto error) =>
        error.Details is { ValueKind: JsonValueKind.Object } details
        && details.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number
            ? line.GetInt32()
            : null;

    /// <summary>
    /// Refreshes elapsed times every second and relative times every 30 seconds.
    /// </summary>
    private void Tick()
    {
        _ticks++;
        foreach (var machine in _machines.Values.Where(machine =>
            machine.State == MachineState.Waking || _ticks % 30 == 0))
        {
            machine.Refresh();
        }
    }

    /// <summary>
    /// Shows a transient message.
    /// </summary>
    /// <param name="notification">The message.</param>
    private void Notify(Notification notification) => Notified?.Invoke(this, notification);

    /// <summary>
    /// Refreshes the section summaries and tells the window that states changed.
    /// </summary>
    private void OnStatesChanged()
    {
        foreach (var section in Sections)
        {
            section.Refresh();
        }
        StatesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One <c>wol wake --wait</c> process and the machines waiting in it.
    /// </summary>
    /// <param name="machines">Machines woken by the run.</param>
    /// <param name="title">Group name, selection or machine name.</param>
    private sealed class WakeRun(IReadOnlyList<MachineItem> machines, string title)
    {
        /// <summary>
        /// Machines woken by the run.
        /// </summary>
        public IReadOnlyList<MachineItem> Machines { get; } = machines;

        /// <summary>
        /// Group name, selection or machine name.
        /// </summary>
        public string Title { get; } = title;

        /// <summary>
        /// When the packets were sent.
        /// </summary>
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

        /// <summary>
        /// Stops waiting (kills the process).
        /// </summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>
        /// Machines that came online.
        /// </summary>
        public HashSet<MachineItem> Online { get; } = [];

        /// <summary>
        /// Machines that did not come online in time.
        /// </summary>
        public HashSet<MachineItem> TimedOut { get; } = [];
    }
}
