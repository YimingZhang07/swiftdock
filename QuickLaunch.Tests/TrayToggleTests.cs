using QuickLaunch.Services;
using QuickLaunch.Views;
using Xunit;
using static QuickLaunch.Views.LauncherWindow;

namespace QuickLaunch.Tests;

public sealed class TrayToggleTests
{
    [Theory]
    [InlineData(125)] [InlineData(500)] [InlineData(1000)]
    public void SingleDoubleAndBurstClicksUseSystemIntervalWithoutExtendingIt(int interval)
    {
        long now = 0;
        var policy = new TrayTogglePolicy(() => now, () => interval);
        Assert.Equal(ToggleAction.Show, policy.Click(false, false, true));
        now = interval / 4; Assert.Equal(ToggleAction.Ignore, policy.Click(true, false, true)); // Double-click.
        now = interval / 2; Assert.Equal(ToggleAction.Ignore, policy.Click(true, false, true));
        now = interval - 1; Assert.Equal(ToggleAction.Ignore, policy.Click(true, false, true));
        now = interval; Assert.Equal(ToggleAction.Hide, policy.Click(true, false, true));
        now += interval; Assert.Equal(ToggleAction.Show, policy.Click(false, false, true));
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(250, true)]
    [InlineData(251, false)]
    public void MouseDownLatchesRecentActivationEvenWhenMouseUpIsLater(int delay, bool hide)
    {
        long now = 1000;
        var policy = new TrayTogglePolicy(() => now, () => 500);
        policy.Deactivated(); now += delay; policy.MouseDown(active: false);
        now += 2000; // Holding the button must not turn Hide into Show.
        Assert.Equal(hide ? ToggleAction.Hide : ToggleAction.Show, policy.Click(true, false, false));
        now += 500; policy.MouseDown(active: false);
        Assert.Equal(ToggleAction.Show, policy.Click(true, false, false)); // Previous activation was consumed.
    }

    [Fact]
    public void CoveredUnpinnedPanelShowsThenNextActiveClickHides()
    {
        long now = 0;
        var policy = new TrayTogglePolicy(() => now, () => 500);
        policy.MouseDown(active: false);
        Assert.Equal(ToggleAction.Show, policy.Click(true, false, false));
        now = 600; policy.MouseDown(active: true);
        Assert.Equal(ToggleAction.Hide, policy.Click(true, false, false));
    }

    [Fact]
    public void GuardsReuseHotkeyPriorityAndIgnoredInteractionsDoNotConsumeDebounce()
    {
        var policy = new TrayTogglePolicy(() => 0, () => 500);
        Assert.Equal(ToggleAction.Ignore, policy.Click(true, false, true, drag: true));
        Assert.Equal(ToggleAction.Ignore, policy.Click(true, false, true, menu: true));
        Assert.Equal(ToggleAction.Ignore, policy.Click(true, false, true, moving: true));
        Assert.Equal(ToggleAction.Ignore, policy.Click(true, false, true, sizeMove: true));
        Assert.Equal(ToggleAction.ActivateOwnedDialog, policy.Click(true, false, true, ownedDialog: true, drag: true));
        Assert.Equal(ToggleAction.ActivateOwnedDialog, policy.Click(true, false, true, ownedDialog: true));
        Assert.Equal(ToggleAction.Hide, policy.Click(true, false, true));
        Assert.Equal(ToggleAction.ActivateOwnedDialog, policy.Click(true, false, true, ownedDialog: true));
    }
}
