using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wol.Desktop.Models;

/// <summary>
/// Base of objects whose changes are shown by bindings.
/// </summary>
public abstract partial class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Tells bindings that every property may have changed (computed display properties
    /// included).
    /// </summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>
    /// Tells bindings that a property changed.
    /// </summary>
    /// <param name="name">Property name; empty for every property.</param>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
