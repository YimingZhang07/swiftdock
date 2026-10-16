using System.Drawing;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class TrayContextMenuTests
{
    [Fact]
    public void PairedContextCallbacksCannotOpenBeforeMouseUpOrReopenAClosedMenu()
    {
        long now = 0;
        var gesture = new TrayMenuGesture(() => now, () => 500);
        gesture.MouseDown();
        now = 2000; Assert.False(gesture.ContextMenu()); // Even while holding the button.
        Assert.True(gesture.MouseUp());
        now += 250; Assert.False(gesture.ContextMenu()); // First menu may already have closed.
        Assert.False(gesture.MouseUp());
        gesture.MouseDown(); Assert.True(gesture.MouseUp()); // A distinct physical gesture.
    }

    [Fact]
    public void KeyboardContextMenuWorksWithoutMouseAndAfterThePairedCallbackInterval()
    {
        long now = 0;
        var gesture = new TrayMenuGesture(() => now, () => 500);
        Assert.True(gesture.ContextMenu());
        gesture.MouseDown(); Assert.True(gesture.MouseUp());
        now = 500; Assert.False(gesture.ContextMenu());
        now = 501; Assert.True(gesture.ContextMenu());
    }

    [Theory]
    [InlineData(3309, 1697)]
    [InlineData(-3309, 1697)]
    public void MouseUsesPhysicalClickEvenWhenShellAnchorIsElsewhere(int x, int y)
    {
        var click = new Point(x, y);
        Assert.Equal(click, TrayContextMenu.SelectAnchor(true, click,
            new Rectangle(3700, 2050, 48, 48)));
    }

    [Fact]
    public void KeyboardUsesIconInsteadOfUnrelatedCursor()
    {
        Assert.Equal(new Point(-3309, 1697), TrayContextMenu.SelectAnchor(false, new Point(800, 400),
            new Rectangle(-3309, 1697, 48, 48)));
    }

    [Fact]
    public void MissingNativePositionDoesNotUseTheOtherInputSourcesPosition()
    {
        Assert.Null(TrayContextMenu.SelectAnchor(false, new Point(800, 400), null));
        Assert.Null(TrayContextMenu.SelectAnchor(false, new Point(800, 400), Rectangle.Empty));
        Assert.Null(TrayContextMenu.SelectAnchor(true, null, new Rectangle(3700, 2050, 48, 48)));
    }
}
