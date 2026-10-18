using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wol.Desktop.Models;
using Wol.Desktop.Services;

namespace Wol.Desktop.Controls;

/// <summary>
/// Shows a machine state as a pill badge: its glyph and label on its background tint.
/// </summary>
public sealed partial class StateBadge : UserControl
{
    /// <summary>
    /// Identifies the <see cref="State"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(MachineState),
        typeof(StateBadge),
        new PropertyMetadata(MachineState.Unknown, (target, _) => ((StateBadge)target).Update()));

    /// <summary>
    /// Identifies the <see cref="Text"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(StateBadge),
        new PropertyMetadata(string.Empty, (target, _) => ((StateBadge)target).Update()));

    /// <summary>
    /// Builds the badge, showing the Unknown state.
    /// </summary>
    public StateBadge()
    {
        InitializeComponent();
        Update();
    }

    /// <summary>
    /// State to show.
    /// </summary>
    public MachineState State
    {
        get => (MachineState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// Label of the badge, such as "Never seen"; the name of the state when empty.
    /// </summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Shows the tint, glyph and label of the current state.
    /// </summary>
    private void Update()
    {
        Label.Text = Text.Length > 0 ? Text : Strings.Get($"State_{State}");
        VisualStateManager.GoToState(this, State.ToString(), useTransitions: false);
    }
}
