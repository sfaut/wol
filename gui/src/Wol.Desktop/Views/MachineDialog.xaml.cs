using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wol.Desktop.Cli;
using Wol.Desktop.Models;
using Wol.Desktop.Services;
using Wol.Desktop.ViewModels;

namespace Wol.Desktop.Views;

/// <summary>
/// Adds a machine, or edits one. Fields are checked as they are filled so that the CLI, which
/// has the final say, rarely refuses a change; the machine is saved before the dialog closes.
/// </summary>
public sealed partial class MachineDialog : ContentDialog
{
    /// <summary>
    /// Longest machine or group name, in characters.
    /// </summary>
    private const int MaxNameLength = 64;

    /// <summary>
    /// Longest interface label, in characters.
    /// </summary>
    private const int MaxLabelLength = 32;

    /// <summary>
    /// App state, which saves the machine.
    /// </summary>
    private readonly MainViewModel _viewModel;

    /// <summary>
    /// Machine edited; null when adding one.
    /// </summary>
    private readonly MachineItem? _editing;

    /// <summary>
    /// Interface rows, in order.
    /// </summary>
    private readonly List<InterfaceRow> _rows = [];

    /// <summary>
    /// Fields the user has left at least once: their errors are shown from then on.
    /// </summary>
    private readonly HashSet<object> _touched = [];

    /// <summary>
    /// Whether a name was typed at least once (the name is required from then on).
    /// </summary>
    private bool _named;

    /// <summary>
    /// Builds the dialog.
    /// </summary>
    /// <param name="viewModel">App state, which saves the machine.</param>
    /// <param name="editing">Machine to edit; null to add one.</param>
    /// <param name="template">Machine whose fields fill a new one (Duplicate); null for none.</param>
    /// <param name="group">Group pre-filled for a new machine; null for none.</param>
    public MachineDialog(MainViewModel viewModel, MachineItem? editing, MachineDto? template = null, string? group = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _editing = editing;

        Title = Strings.Get(editing is null ? "MachineDialog_AddTitle" : "MachineDialog_EditTitle");
        PrimaryButtonText = Strings.Get(editing is null ? "MachineDialog_Add" : "MachineDialog_Save");
        CloseButtonText = Strings.Get("MachineDialog_Cancel");

        foreach (var name in viewModel.GroupNames)
        {
            GroupBox.Items.Add(name);
        }

        var source = editing?.Definition ?? template;
        if (source is not null)
        {
            NameBox.Text = editing is null ? Strings.Format("Duplicate_Name", source.Name) : source.Name;
            if (source.Group is { } current)
            {
                GroupBox.SelectedItem = viewModel.GroupNames.FirstOrDefault(name => name == current);
            }
            foreach (var item in source.Interfaces)
            {
                AddRow(item.Mac, item.Label);
            }
            BroadcastBox.Text = source.Broadcast ?? string.Empty;
            if (source.Port is { } port)
            {
                PortBox.Value = port;
            }
            AdvancedExpander.IsExpanded = source.Broadcast is not null || source.Port is not null;
        }
        else
        {
            if (group is not null)
            {
                GroupBox.SelectedItem = viewModel.GroupNames.FirstOrDefault(name => name == group);
            }
            AddRow(string.Empty, null);
        }

        Validate();
    }

    /// <summary>
    /// Text of the group box: the item picked or the text typed.
    /// </summary>
    private string GroupText => (GroupBox.SelectedItem as string ?? GroupBox.Text ?? string.Empty).Trim();

    /// <summary>
    /// Normalizes a MAC address written in one of the notations the CLI accepts
    /// (<c>AA:BB:CC:DD:EE:FF</c>, <c>AA-BB-…</c>, <c>AABB.CCDD.EEFF</c>, <c>AABBCCDDEEFF</c>).
    /// </summary>
    /// <param name="text">Text typed.</param>
    /// <returns>The address as <c>AA:BB:CC:DD:EE:FF</c>, or null when it is not one.</returns>
    public static string? NormalizeMac(string text)
    {
        var match = MacPattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var hex = Regex.Replace(match.Value, "[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();
        return string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }

    /// <summary>
    /// Accepted MAC address notations, with a consistent separator.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^(?:[0-9A-Fa-f]{2}(?<s>[:-])(?:[0-9A-Fa-f]{2}\k<s>){4}[0-9A-Fa-f]{2}|[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}|[0-9A-Fa-f]{12})$")]
    private static partial Regex MacPattern();

    /// <summary>
    /// Number of characters of a text, as the CLI counts them (Unicode scalar values).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The number of characters.</returns>
    private static int Length(string text) => text.EnumerateRunes().Count();

    /// <summary>
    /// Adds an interface row.
    /// </summary>
    /// <param name="mac">MAC address shown.</param>
    /// <param name="label">Label shown, if any.</param>
    /// <returns>The new row.</returns>
    private InterfaceRow AddRow(string mac, string? label)
    {
        var row = new InterfaceRow();
        row.Mac.Text = mac;
        if (label is not null)
        {
            // A suggested label is picked; any other text is typed once the box has its template
            // (an editable ComboBox drops text set before).
            var suggested = row.Label.Items.OfType<string>().FirstOrDefault(item => item == label);
            if (suggested is not null)
            {
                row.Label.SelectedItem = suggested;
            }
            else
            {
                row.Label.Loaded += (_, _) =>
                {
                    row.Label.Text = label;
                    Validate();
                };
            }
        }
        row.Mac.TextChanged += (_, _) => Validate();
        row.Mac.LostFocus += (_, _) =>
        {
            _touched.Add(row.Mac);
            Validate();
        };
        row.Label.TextSubmitted += (_, _) => Validate();
        row.Label.LostFocus += (_, _) =>
        {
            _touched.Add(row.Label);
            Validate();
        };
        row.Label.SelectionChanged += (_, _) => Validate();
        row.Remove.Click += (_, _) => RemoveRow(row);

        _rows.Add(row);
        InterfacesPanel.Children.Add(row.Root);
        Validate();
        return row;
    }

    /// <summary>
    /// Removes an interface row and focuses the next one.
    /// </summary>
    /// <param name="row">Row to remove.</param>
    private void RemoveRow(InterfaceRow row)
    {
        if (_rows.Count <= 1)
        {
            return;
        }

        var index = _rows.IndexOf(row);
        _rows.Remove(row);
        InterfacesPanel.Children.Remove(row.Root);
        _rows[Math.Min(index, _rows.Count - 1)].Mac.Focus(FocusState.Programmatic);
        Validate();
    }

    /// <summary>
    /// Checks every field, shows the messages of the fields already touched, and enables the
    /// primary button when the machine can be saved.
    /// </summary>
    /// <param name="showAll">Whether to show every error, as on submit.</param>
    /// <returns>The machine to save, or null when a field is invalid.</returns>
    private MachineForm? Validate(bool showAll = false)
    {
        var valid = true;

        void Show(StackPanel host, object field, MessageKind kind, string? text)
        {
            if (kind == MessageKind.Error)
            {
                valid = false;
            }
            var visible = kind is MessageKind.Warning or MessageKind.Info || showAll || _touched.Contains(field);
            SetMessage(host, visible ? kind : MessageKind.None, text);
        }

        // Name.
        var name = NameBox.Text.Trim();
        var other = _viewModel.FindMachine(name);
        if (name.Length == 0)
        {
            // Only once a name was typed then erased: leaving the empty field is not an error yet.
            Show(NameMessage, _named ? NameBox : this, MessageKind.Error, Strings.Get("Validation_Name_Required"));
        }
        else if (Length(name) > MaxNameLength)
        {
            Show(NameMessage, NameBox, MessageKind.Error, Strings.Get("Validation_Name_TooLong"));
        }
        else if (other is not null && other != _editing)
        {
            Show(NameMessage, NameBox, MessageKind.Error, Strings.Format("Validation_Name_Duplicate", other.Name));
        }
        else
        {
            Show(NameMessage, NameBox, MessageKind.None, null);
        }

        // Group: an existing one, or a new one created on save.
        var groupText = GroupText;
        var group = groupText.Length == 0
            ? null
            : _viewModel.GroupNames.FirstOrDefault(existing =>
                string.Equals(existing, groupText, StringComparison.OrdinalIgnoreCase));
        var createGroup = groupText.Length > 0 && group is null;
        if (createGroup && IsReservedGroup(groupText))
        {
            Show(GroupMessage, GroupBox, MessageKind.Error, Strings.Format("Validation_Group_Reserved", groupText));
        }
        else if (createGroup && Length(groupText) > MaxNameLength)
        {
            Show(GroupMessage, GroupBox, MessageKind.Error, Strings.Get("Validation_Name_TooLong"));
        }
        else if (createGroup)
        {
            Show(GroupMessage, GroupBox, MessageKind.Info, Strings.Format("MachineDialog_Group_New", groupText));
        }
        else
        {
            Show(GroupMessage, GroupBox, MessageKind.None, null);
        }

        // Interfaces.
        var interfaces = new List<InterfaceDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var number = index + 1;
            row.Remove.IsEnabled = _rows.Count > 1;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Mac, Strings.Format("MachineDialog_Interface_Mac_Name", number));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Label, Strings.Format("MachineDialog_Interface_Label_Name", number));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                row.Remove,
                Strings.Format(_rows.Count > 1 ? "MachineDialog_Interface_Remove_Name" : "MachineDialog_Interface_Remove_LastName", number));

            var text = row.Mac.Text.Trim();
            var mac = NormalizeMac(text);
            var label = (row.Label.SelectedItem as string ?? row.Label.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                Show(row.Message, row.Mac, MessageKind.Error, Strings.Get("Validation_Mac_Required"));
            }
            else if (mac is null)
            {
                Show(row.Message, row.Mac, MessageKind.Error, Strings.Get("Validation_Mac_Invalid"));
            }
            else if (!seen.Add(mac))
            {
                Show(row.Message, row.Mac, MessageKind.Error, Strings.Get("Validation_Mac_Repeated"));
            }
            else if (Length(label) > MaxLabelLength)
            {
                Show(row.Message, row.Label, MessageKind.Error, Strings.Get("Validation_Label_TooLong"));
            }
            else if (_viewModel.AllMachines.FirstOrDefault(machine => machine != _editing
                && machine.Definition.Interfaces.Any(item => item.Mac == mac)) is { } sharing)
            {
                Show(row.Message, row.Mac, MessageKind.Warning, Strings.Format("Validation_Mac_Shared", sharing.Name));
            }
            else if (mac != text)
            {
                Show(row.Message, row.Mac, MessageKind.Info, Strings.Format("MachineDialog_Interface_Mac_Preview", mac));
            }
            else
            {
                Show(row.Message, row.Mac, MessageKind.None, null);
            }

            if (mac is not null)
            {
                interfaces.Add(new InterfaceDto(mac, label.Length > 0 ? label : null));
            }
        }

        // Advanced.
        var broadcast = BroadcastBox.Text.Trim();
        if (broadcast.Length > 0 && !IsIPv4(broadcast))
        {
            Show(BroadcastMessage, BroadcastBox, MessageKind.Error, Strings.Get("Validation_Broadcast_Invalid"));
        }
        else
        {
            Show(BroadcastMessage, BroadcastBox, MessageKind.None, null);
        }

        int? port = null;
        if (!double.IsNaN(PortBox.Value))
        {
            if (PortBox.Value is >= 1 and <= 65535 && PortBox.Value == Math.Floor(PortBox.Value))
            {
                port = (int)PortBox.Value;
                Show(PortMessage, PortBox, MessageKind.None, null);
            }
            else
            {
                Show(PortMessage, PortBox, MessageKind.Error, Strings.Get("Validation_Port_Invalid"));
            }
        }
        else
        {
            Show(PortMessage, PortBox, MessageKind.None, null);
        }

        IsPrimaryButtonEnabled = valid;
        return valid
            ? new MachineForm(name, group ?? (createGroup ? groupText : null), createGroup, interfaces, broadcast.Length > 0 ? broadcast : null, port)
            : null;
    }

    /// <summary>
    /// Tells whether a group name is reserved for the machines without a group.
    /// </summary>
    /// <param name="name">Group name.</param>
    /// <returns>True when it is "Ungrouped", in English or in the current language.</returns>
    public static bool IsReservedGroup(string name) =>
        string.Equals(name, "Ungrouped", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, Strings.Get("Nav_Ungrouped"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tells whether a text is an IPv4 address in dotted notation.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>True for an address such as 192.168.1.255.</returns>
    private static bool IsIPv4(string text) =>
        text.Count(character => character == '.') == 3
        && IPAddress.TryParse(text, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>
    /// Shows a message under a field: error, warning or information.
    /// </summary>
    /// <param name="host">Panel under the field.</param>
    /// <param name="kind">Kind of message; None hides it.</param>
    /// <param name="text">Text of the message.</param>
    internal static void SetMessage(StackPanel host, MessageKind kind, string? text)
    {
        host.Children.Clear();
        if (kind == MessageKind.None || text is null)
        {
            host.Visibility = Visibility.Collapsed;
            return;
        }

        host.Orientation = Orientation.Horizontal;
        host.Spacing = 6;
        host.Visibility = Visibility.Visible;
        if (kind != MessageKind.Info)
        {
            host.Children.Add(new FontIcon
            {
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0),
                Glyph = kind == MessageKind.Error ? "\uEA39" : "\uE7BA",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    kind == MessageKind.Error ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"],
            });
        }
        host.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = kind == MessageKind.Info
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
    }

    /// <summary>
    /// Saves the machine; keeps the dialog open when a field is invalid or the CLI refuses it.
    /// </summary>
    /// <param name="sender">The dialog.</param>
    /// <param name="args">Lets the dialog stay open.</param>
    private async void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Validate(showAll: true) is not { } form)
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        var error = _editing is null
            ? await _viewModel.AddMachineAsync(form)
            : await _viewModel.EditMachineAsync(_editing, form);
        if (error is not null)
        {
            args.Cancel = true;
            SetMessage(NameMessage, MessageKind.Error, error);
            NameBox.Focus(FocusState.Programmatic);
            IsPrimaryButtonEnabled = true;
        }
        deferral.Complete();
    }

    /// <summary>
    /// Shows the name errors once the field is left.
    /// </summary>
    /// <param name="sender">The name box.</param>
    /// <param name="args">Unused.</param>
    private void NameBox_LostFocus(object sender, RoutedEventArgs args)
    {
        _touched.Add(NameBox);
        Validate();
    }

    /// <summary>
    /// Checks the form as a field changes.
    /// </summary>
    /// <param name="sender">The field.</param>
    /// <param name="args">Unused.</param>
    private void Field_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (ReferenceEquals(sender, BroadcastBox))
        {
            _touched.Add(BroadcastBox);
        }
        else if (ReferenceEquals(sender, NameBox) && NameBox.Text.Length > 0)
        {
            _named = true;
        }
        Validate();
    }

    /// <summary>
    /// Checks the port as it changes.
    /// </summary>
    /// <param name="sender">The port box.</param>
    /// <param name="args">Unused.</param>
    private void PortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        _touched.Add(PortBox);
        Validate();
    }

    /// <summary>
    /// Checks the group when one is picked.
    /// </summary>
    /// <param name="sender">The group box.</param>
    /// <param name="args">Unused.</param>
    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs args) => Validate();

    /// <summary>
    /// Keeps a typed group name, existing or new.
    /// </summary>
    /// <param name="sender">The group box.</param>
    /// <param name="args">The text typed.</param>
    private void GroupBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        var text = args.Text.Trim();
        var existing = _viewModel.GroupNames.FirstOrDefault(name =>
            string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            sender.SelectedItem = existing;
        }
        else
        {
            sender.SelectedItem = null;
            args.Handled = true;
        }
        _touched.Add(GroupBox);
        DispatcherQueue.TryEnqueue(() => Validate());
    }

    /// <summary>
    /// Adds an empty interface row and focuses it.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="args">Unused.</param>
    private void AddInterface_Click(object sender, RoutedEventArgs args) =>
        AddRow(string.Empty, null).Mac.Focus(FocusState.Programmatic);

    /// <summary>
    /// Scans the local network for devices no machine uses yet.
    /// </summary>
    /// <param name="sender">The flyout.</param>
    /// <param name="args">Unused.</param>
    private async void NetworkFlyout_Opening(object sender, object args)
    {
        DevicesList.Items.Clear();
        DevicesList.Visibility = Visibility.Collapsed;
        DiscoveryMessage.Visibility = Visibility.Collapsed;
        ScanningPanel.Visibility = Visibility.Visible;

        var (devices, error) = await _viewModel.ScanAsync();
        var used = _rows.Select(row => NormalizeMac(row.Mac.Text)).OfType<string>().ToHashSet();
        foreach (var device in devices.Where(device => !used.Contains(device.Mac)))
        {
            var item = new StackPanel { Tag = device, Padding = new Thickness(0, 4, 0, 4) };
            item.Children.Add(new TextBlock { Text = device.Mac });
            item.Children.Add(new TextBlock
            {
                Text = device.Address,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                item,
                Strings.Format("Discovery_Item_Name", device.Mac, device.Address));
            DevicesList.Items.Add(item);
        }

        ScanningPanel.Visibility = Visibility.Collapsed;
        if (DevicesList.Items.Count > 0)
        {
            DevicesList.Visibility = Visibility.Visible;
            return;
        }
        DiscoveryMessage.Text = error is null ? Strings.Get("Discovery_Empty") : Strings.Describe(error);
        DiscoveryMessage.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Fills the empty row (or a new one) with the device picked.
    /// </summary>
    /// <param name="sender">The device list.</param>
    /// <param name="args">Device picked.</param>
    private void DevicesList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not FrameworkElement { Tag: DeviceDto device })
        {
            return;
        }

        var row = _rows.FirstOrDefault(row => row.Mac.Text.Trim().Length == 0) ?? AddRow(string.Empty, null);
        row.Mac.Text = device.Mac;
        _touched.Add(row.Mac);
        NetworkFlyout.Hide();
        row.Label.Focus(FocusState.Programmatic);
        Validate();
    }

    /// <summary>
    /// Kind of message shown under a field.
    /// </summary>
    internal enum MessageKind
    {
        /// <summary>
        /// No message.
        /// </summary>
        None,

        /// <summary>
        /// Help, e.g. the normalized MAC address.
        /// </summary>
        Info,

        /// <summary>
        /// Does not block saving.
        /// </summary>
        Warning,

        /// <summary>
        /// Blocks saving.
        /// </summary>
        Error,
    }

    /// <summary>
    /// Controls of one interface row: MAC address, label, remove button, message.
    /// </summary>
    private sealed class InterfaceRow
    {
        /// <summary>
        /// Builds the row: Grid * | 148 | 32, the message spanning the three columns.
        /// </summary>
        public InterfaceRow()
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Mac.PlaceholderText = Strings.Get("MachineDialog_Interface_Mac_Placeholder");
            Label.PlaceholderText = Strings.Get("MachineDialog_Interface_Label_Placeholder");
            Label.Items.Add(Strings.Get("MachineDialog_Interface_Label_Ethernet"));
            Label.Items.Add(Strings.Get("MachineDialog_Interface_Label_WiFi"));
            Remove.Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 };

            Grid.SetColumn(Label, 1);
            Grid.SetColumn(Remove, 2);
            Grid.SetRow(Message, 1);
            Grid.SetColumnSpan(Message, 3);
            Root.Children.Add(Mac);
            Root.Children.Add(Label);
            Root.Children.Add(Remove);
            Root.Children.Add(Message);
        }

        /// <summary>
        /// Root of the row.
        /// </summary>
        public Grid Root { get; } = new() { ColumnSpacing = 8, RowSpacing = 4 };

        /// <summary>
        /// MAC address.
        /// </summary>
        public TextBox Mac { get; } = new();

        /// <summary>
        /// Label, picked or typed.
        /// </summary>
        public ComboBox Label { get; } = new() { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };

        /// <summary>
        /// Removes the row.
        /// </summary>
        public Button Remove { get; } = new() { Width = 32, Height = 32, Padding = new Thickness(0) };

        /// <summary>
        /// Message about the row.
        /// </summary>
        public StackPanel Message { get; } = new() { Visibility = Visibility.Collapsed };
    }
}
