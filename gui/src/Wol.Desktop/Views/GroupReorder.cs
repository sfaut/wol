using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Wol.Desktop.Models;
using Wol.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Wol.Desktop.Views;

/// <summary>
/// Reorders groups by drag and drop, on the navigation pane items and on the group cards of the
/// machine list: a group dropped on the upper or lower half of another one moves above or below
/// it (<c>wol group move</c>), and an accent line shows where it lands. One instance serves the
/// whole window, so a group can be dragged from one place and dropped on the other.
/// </summary>
/// <remarks>
/// Navigation items and expander headers capture the pointer when pressed, which keeps
/// <c>CanDrag</c> from ever starting a drag: the drag is started by hand once the pointer moves.
/// Drag and drop fails in an elevated process (WinUI 3).
/// </remarks>
public sealed class GroupReorder
{
    /// <summary>
    /// Distance the pointer moves, in pixels, before pressing an element becomes a drag.
    /// </summary>
    private const double DragThreshold = 4;

    /// <summary>
    /// App state; it runs the moves.
    /// </summary>
    private readonly MainViewModel _viewModel;

    /// <summary>
    /// Overlay holding the marker.
    /// </summary>
    private readonly Canvas _layer;

    /// <summary>
    /// Accent line showing where the dragged group would land.
    /// </summary>
    private readonly Rectangle _marker;

    /// <summary>
    /// Elements already set up, so recycled elements are not set up twice.
    /// </summary>
    private readonly ConditionalWeakTable<UIElement, object> _attached = [];

    /// <summary>
    /// Group being dragged, if any.
    /// </summary>
    private GroupSection? _dragged;

    /// <summary>
    /// Where the left button went down on an element, until a drag starts or the button is released.
    /// </summary>
    private Point? _origin;

    /// <summary>
    /// Builds the drag and drop handling of a window.
    /// </summary>
    /// <param name="viewModel">App state; it runs the moves.</param>
    /// <param name="layer">Overlay holding the marker, above every element set up.</param>
    /// <param name="marker">Accent line showing where the dragged group would land.</param>
    public GroupReorder(MainViewModel viewModel, Canvas layer, Rectangle marker)
    {
        _viewModel = viewModel;
        _layer = layer;
        _marker = marker;
    }

    /// <summary>
    /// Lets an element showing a group be dragged and receive dropped groups; does nothing when it
    /// is already set up.
    /// </summary>
    /// <param name="element">Navigation item or group card.</param>
    /// <param name="section">
    ///     Gives the group the element shows at the time of the event (elements are recycled), or
    ///     null when it is not a named group.
    /// </param>
    /// <param name="canStart">
    ///     Tells whether a press may start a drag (e.g. not on a button inside the element); always
    ///     when null.
    /// </param>
    /// <param name="gap">Distance from the element edge to the marker center, in pixels.</param>
    /// <param name="inset">Distance from the element sides to the marker ends, in pixels.</param>
    public void Attach(
        FrameworkElement element,
        Func<GroupSection?> section,
        Func<PointerRoutedEventArgs, bool>? canStart,
        double gap,
        double inset)
    {
        if (_attached.TryGetValue(element, out _))
        {
            return;
        }
        _attached.Add(element, element);

        element.AllowDrop = true;
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(element);
            _origin = point.Properties.IsLeftButtonPressed && section() is not null && canStart?.Invoke(args) != false
                ? point.Position
                : null;
        }), handledEventsToo: true);
        element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) => Pointer_Moved(element, args)), handledEventsToo: true);
        element.DragStarting += (_, args) => Drag_Starting(section(), args);
        element.DropCompleted += (_, _) =>
        {
            _dragged = null;
            HideMarker();
        };
        element.DragOver += (_, args) => Drag_Over(element, section(), args, gap, inset);
        element.DragLeave += (_, _) => HideMarker();
        element.Drop += (_, args) => Dropped(element, section(), args);
    }

    /// <summary>
    /// Starts dragging once the pointer moved far enough with the button down.
    /// </summary>
    /// <param name="element">Element pressed.</param>
    /// <param name="args">Pointer.</param>
    private void Pointer_Moved(UIElement element, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(element);
        if (_origin is not { } origin || !point.Properties.IsLeftButtonPressed)
        {
            _origin = null;
            return;
        }

        if (Math.Abs(point.Position.X - origin.X) > DragThreshold || Math.Abs(point.Position.Y - origin.Y) > DragThreshold)
        {
            _origin = null;
            _ = StartDragAsync(element, point);
        }
    }

    /// <summary>
    /// Starts a drag, ignoring a failure (elevated process): the press then stays a press.
    /// </summary>
    /// <param name="element">Element dragged.</param>
    /// <param name="point">Pointer.</param>
    /// <returns>A task completing when the drag ends.</returns>
    private static async Task StartDragAsync(UIElement element, PointerPoint point)
    {
        try
        {
            await element.StartDragAsync(point);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            //
        }
    }

    /// <summary>
    /// Fills the drag data; cancels the drag when the CLI cannot be used.
    /// </summary>
    /// <param name="section">Group dragged.</param>
    /// <param name="args">Drag data.</param>
    private void Drag_Starting(GroupSection? section, DragStartingEventArgs args)
    {
        if (section is null || !_viewModel.CanUseCli)
        {
            args.Cancel = true;
            return;
        }

        _dragged = section;
        args.Data.SetText(section.Title);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    /// <summary>
    /// Accepts the dragged group over another one and shows where it would land.
    /// </summary>
    /// <param name="element">Element under the pointer.</param>
    /// <param name="section">Its group.</param>
    /// <param name="args">Pointer position; operation accepted.</param>
    /// <param name="gap">Distance from the element edge to the marker center, in pixels.</param>
    /// <param name="inset">Distance from the element sides to the marker ends, in pixels.</param>
    private void Drag_Over(FrameworkElement element, GroupSection? section, DragEventArgs args, double gap, double inset)
    {
        var before = args.GetPosition(element).Y < element.ActualHeight / 2;
        if (DropPosition(section, before) is null)
        {
            args.AcceptedOperation = DataPackageOperation.None;
            HideMarker();
            return;
        }

        args.AcceptedOperation = DataPackageOperation.Move;
        args.DragUIOverride.IsCaptionVisible = false;
        args.DragUIOverride.IsGlyphVisible = false;
        args.Handled = true;

        var origin = element.TransformToVisual(_layer).TransformPoint(default);
        _marker.Width = Math.Max(0, element.ActualWidth - 2 * inset);
        Canvas.SetLeft(_marker, origin.X + inset);
        Canvas.SetTop(_marker, (before ? origin.Y - gap : origin.Y + element.ActualHeight + gap) - _marker.Height / 2);
        _marker.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Moves the dragged group above or below the group it is dropped on.
    /// </summary>
    /// <param name="element">Element dropped on.</param>
    /// <param name="section">Its group.</param>
    /// <param name="args">Pointer position.</param>
    private void Dropped(FrameworkElement element, GroupSection? section, DragEventArgs args)
    {
        HideMarker();
        var position = DropPosition(section, args.GetPosition(element).Y < element.ActualHeight / 2);
        if (_dragged is { } dragged && position is not null)
        {
            args.Handled = true;
            _ = _viewModel.MoveGroupToAsync(dragged, position.Value);
        }
        _dragged = null;
    }

    /// <summary>
    /// Computes where the dragged group lands when dropped next to a group.
    /// </summary>
    /// <param name="target">Group under the pointer, null when it is not a named group.</param>
    /// <param name="before">Whether the drop is on the upper half of the target (above it).</param>
    /// <returns>The new 1-based position, or null when no group is dragged or it would not move.</returns>
    private int? DropPosition(GroupSection? target, bool before)
    {
        if (_dragged is not { } dragged || target is null || dragged == target)
        {
            return null;
        }

        var groups = _viewModel.Sections.Where(section => section.Group is not null).ToList();
        var from = groups.IndexOf(dragged);
        var to = groups.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return null;
        }

        // Index of the target once the dragged group is taken out of the list.
        var index = to > from ? to - 1 : to;
        var position = (before ? index : index + 1) + 1;
        return position == from + 1 ? null : position;
    }

    /// <summary>
    /// Removes the accent line.
    /// </summary>
    private void HideMarker() => _marker.Visibility = Visibility.Collapsed;
}
