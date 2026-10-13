using System.IO;
using QuickLaunch.Models;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class HotkeyAndInstanceTests
{
    private sealed class Backend : IHotkeyBackend
    {
        public bool Fail { get; set; }
        public HashSet<int> Registered { get; } = [];
        public uint Flags { get; private set; }
        public bool Register(int id, uint modifiers, uint key, out int error)
        {
            Flags = modifiers;
            error = Fail ? 1409 : 0;
            if (Fail) return false;
            Registered.Add(id);
            return true;
        }
        public void Unregister(int id) => Registered.Remove(id);
    }

    [Fact]
    public void FailedReplacementKeepsOldHotkeyThenSuccessReleasesIt()
    {
        var backend = new Backend();
        using var binding = new HotkeyBinding(backend);
        Assert.True(binding.TryChange(new()));
        int old = binding.ActiveId;
        backend.Fail = true;
        var candidate = new HotkeyConfig { Key = "F8", Modifiers = ["Control", "Shift"] };
        Assert.False(binding.TryChange(candidate));
        Assert.Equal(old, binding.ActiveId);
        Assert.Contains(old, backend.Registered);
        Assert.Equal("Space", binding.Current!.Key);
        backend.Fail = false;
        Assert.True(binding.TryChange(candidate));
        Assert.DoesNotContain(old, backend.Registered);
        Assert.Single(backend.Registered);
        Assert.Equal(0x4006u, backend.Flags);
    }

    [Theory]
    [InlineData("", "F8")] [InlineData("Bogus", "F8")] [InlineData("Alt", "NotAKey")]
    public void InvalidCombinationsRejected(string modifier, string key)
    {
        var config = new HotkeyConfig { Key = key, Modifiers = modifier.Length == 0 ? [] : [modifier] };
        Assert.Throws<InvalidDataException>(() => ConfigValidation.ValidateHotkey(config));
    }

    [Fact]
    public async Task SecondaryForwardsToPrimaryWithoutOpeningAnotherInstance()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-ipc-" + Guid.NewGuid().ToString("N"));
        int shows = 0;
        await using var first = new SingleInstanceService(directory, () => Interlocked.Increment(ref shows));
        await using var second = new SingleInstanceService(directory, () => throw new InvalidOperationException());
        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        await second.NotifyPrimaryAsync();
        Assert.Equal(1, Volatile.Read(ref shows));
    }

    [Fact]
    public async Task DifferentProfilesHaveIndependentInstancesAndDisposalAllowsRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-ipc-" + Guid.NewGuid().ToString("N"));
        var first = new SingleInstanceService(directory, () => { });
        await using var other = new SingleInstanceService(directory + "-other", () => { });
        Assert.True(first.IsPrimary && other.IsPrimary);
        await first.DisposeAsync();
        await using var restarted = new SingleInstanceService(directory, () => { });
        Assert.True(restarted.IsPrimary);
    }

    [Fact]
    public async Task SecondaryTakesOverWhenPrimaryExitsBeforeForwarding()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-ipc-" + Guid.NewGuid().ToString("N"));
        var first = new SingleInstanceService(directory, () => { });
        await using var second = new SingleInstanceService(directory, () => { });
        Assert.False(second.IsPrimary);
        await first.DisposeAsync();
        await second.NotifyPrimaryAsync();
        Assert.True(second.IsPrimary);
    }
}
