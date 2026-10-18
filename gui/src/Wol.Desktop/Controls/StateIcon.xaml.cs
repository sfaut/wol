using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wol.Desktop.Models;

namespace Wol.Desktop.Controls;

/// <summary>
/// Shows the indicator of a machine state.
/// </summary>
public sealed partial class StateIcon : UserControl
{
    /// <summary>
    /// Identifies the <see cref="State"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(MachineState),
        typeof(StateIcon),
        new PropertyMetadata(MachineState.Unknown, (target, _) => ((StateIcon)target).Update()));

    /// <summary>
    /// Builds the indicator, showing the Unknown state.
    /// </summary>
    public StateIcon()
    {
        InitializeComponent();
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
    /// Shows the element of the current state and hides the others.
    /// </summary>
    private void Update()
    {
        static Visibility When(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

        OnlineIcon.Visibility = When(State == MachineState.Online);
        OfflineIcon.Visibility = When(State == MachineState.Offline);
        WakingIcon.Visibility = When(State == MachineState.Waking);
        WakingIcon.IsActive = State == MachineState.Waking;
        TimedOutIcon.Visibility = When(State == MachineState.TimedOut);
        UnknownIcon.Visibility = When(State == MachineState.Unknown);
        ErrorIcon.Visibility = When(State == MachineState.Error);
    }
}
