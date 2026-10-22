using System.Windows;
using Swiftdock.Views;
using Xunit;

namespace Swiftdock.Tests;

public sealed class TabClickGestureTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, -5)]
    [InlineData(4, 6)]
    [InlineData(-4, -6)]
    public void ReleaseWithoutCrossingThresholdClicksOnce(double x, double y)
    {
        var gesture = new TabClickGesture();
        gesture.Begin(new Point(20, 30), 4, 6);
        var release = new Point(20 + x, 30 + y);
        gesture.Move(release);
        Assert.True(gesture.Complete(release, isOver: true));
        Assert.False(gesture.Complete(release, isOver: true));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(-5, 0)]
    [InlineData(0, 7)]
    [InlineData(0, -7)]
    public void DragPastEitherThresholdCannotClickEvenAfterReturning(double x, double y)
    {
        var gesture = new TabClickGesture();
        var origin = new Point(20, 30);
        gesture.Begin(origin, 4, 6);
        gesture.Move(new Point(origin.X + x, origin.Y + y));
        gesture.Move(origin);
        Assert.False(gesture.Complete(origin, isOver: true));
        gesture.Begin(origin, 4, 6);
        Assert.True(gesture.Complete(origin, isOver: true));
    }

    [Fact]
    public void ReleasePositionAlsoChecksThresholdAndOutsideReleaseCancels()
    {
        var gesture = new TabClickGesture();
        gesture.Begin(new Point(), 4, 6);
        Assert.False(gesture.Complete(new Point(5, 0), isOver: true));
        gesture.Begin(new Point(), 4, 6);
        Assert.False(gesture.Complete(new Point(), isOver: false));
        Assert.False(gesture.Complete(new Point(), isOver: true));
    }

    [Fact]
    public void DragStartOrCancelledPressCannotBecomeAClickOnRelease()
    {
        var gesture = new TabClickGesture();
        gesture.Begin(new Point(), 4, 6);
        gesture.Cancel(); // OLE started, mouse left, capture lost, double click, or unload.
        Assert.False(gesture.Complete(new Point(), isOver: true));
    }
}
