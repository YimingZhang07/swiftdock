using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GongDragDrop = GongSolutions.Wpf.DragDrop.DragDrop;

namespace QuickLaunch.Views;

// Gong releases a drag source's capture on mouse move, even below its threshold.
// Select on release without relying on ButtonBase's capture; keep the command,
// automation peer and keyboard behavior, and leave drag initiation to Gong.
public sealed class GroupTabButton : Button
{
    private readonly TabClickGesture _click = new();

    public GroupTabButton()
    {
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(PointerDown), handledEventsToo: true);
        AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(PointerMove), handledEventsToo: true);
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(PointerUp), handledEventsToo: true);
        Unloaded += (_, _) => CancelClick();
    }

    internal void CancelClick() => _click.Cancel();

    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        CancelClick();
        if (e.ClickCount == 1)
            _click.Begin(e.GetPosition(this), GongDragDrop.GetMinimumHorizontalDragDistance(this),
                GongDragDrop.GetMinimumVerticalDragDistance(this));
    }

    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) _click.Move(e.GetPosition(this));
        else CancelClick();
    }

    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        bool click = _click.Complete(e.GetPosition(this), IsMouseOver);
        e.Handled = true;
        if (click && IsEnabled) OnClick();
    }

    // Leave the preview events to Gong, without ButtonBase taking a second capture
    // or producing a second click when dragging is unavailable (e.g. read-only).
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { }
    protected override void OnMouseLeave(MouseEventArgs e) { CancelClick(); base.OnMouseLeave(e); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { CancelClick(); base.OnLostMouseCapture(e); }
}

internal sealed class TabClickGesture
{
    private Point? _origin;
    private double _horizontal, _vertical;

    internal void Begin(Point position, double horizontal, double vertical)
    {
        _origin = position; _horizontal = horizontal; _vertical = vertical;
    }

    internal void Move(Point position)
    {
        // Match Gong: crossing either distance starts a drag. Once crossed, moving
        // back to the original point must not turn a completed/cancelled drag into a click.
        if (_origin is { } origin && (Math.Abs(position.X - origin.X) > _horizontal
            || Math.Abs(position.Y - origin.Y) > _vertical)) Cancel();
    }

    internal bool Complete(Point position, bool isOver)
    {
        Move(position);
        bool click = isOver && _origin is not null;
        Cancel();
        return click;
    }

    internal void Cancel() => _origin = null;
}
