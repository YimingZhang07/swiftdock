using System.IO;
using Swiftdock.Models;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public sealed class HotkeyAndInstanceTests
{
    [Theory]
    [InlineData(1, true, true, 1, 0, 1)]
    [InlineData(2, true, true, 0, 1, 1)]
    [InlineData(2, false, true, 0, 0, 0)]
    [InlineData(2, true, false, 0, 0, 0)]
    [InlineData(1, false, true, 0, 0, 0)]
    [InlineData(0, true, true, 0, 0, 0)]
    [InlineData(255, true, true, 0, 0, 0)]
    public async Task PipeCommandsAcknowledgeBeforeShutdown(byte command, bool accepts, bool supportsExit, int expectedShows, int expectedExits, byte expectedAck)
    {
        using var response = new MemoryStream();
        int shows = 0, exits = 0;
        void Exit()
        {
            Assert.Equal(new byte[] { 1 }, response.ToArray());
            exits++;
        }
        await SingleInstanceService.HandleRequestAsync(command, response, () => shows++, supportsExit ? Exit : null, () => accepts);
        Assert.Equal(new byte[] { expectedAck }, response.ToArray());
        Assert.Equal(expectedShows, shows);
        Assert.Equal(expectedExits, exits);
    }

    [Fact]
    public async Task FailedAcknowledgementDoesNotRequestExit()
    {
        using var response = new MemoryStream(new byte[1], writable: false);
        int exits = 0;
        await Assert.ThrowsAsync<NotSupportedException>(() => SingleInstanceService.HandleRequestAsync(2, response, () => { }, () => exits++, () => true));
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task ExitWithoutAnInstanceDoesNotCreateAProfileOrClaimPrimary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-exit-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(2, await SingleInstanceService.RequestExitAsync(directory, TimeSpan.FromMilliseconds(60)));
        Assert.False(Directory.Exists(directory));
        await using var first = new SingleInstanceService(directory, () => { });
        Assert.True(first.IsPrimary);
        Assert.Equal(SingleInstanceService.GetIdentity(directory), SingleInstanceService.GetIdentity(directory.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.NotEqual(SingleInstanceService.GetIdentity(directory), SingleInstanceService.GetIdentity(directory + "-other"));
    }

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
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-ipc-" + Guid.NewGuid().ToString("N"));
        int shows = 0;
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new SingleInstanceService(directory, () => Interlocked.Increment(ref shows), exitRequested: () => exit.TrySetResult());
        await using var second = new SingleInstanceService(directory, () => throw new InvalidOperationException());
        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        await second.NotifyPrimaryAsync();
        Assert.Equal(1, Volatile.Read(ref shows));
        Assert.Equal(2, await SingleInstanceService.RequestExitAsync(directory + "-other", TimeSpan.FromMilliseconds(60)));
        Assert.False(exit.Task.IsCompleted);
        Assert.Equal(0, await SingleInstanceService.RequestExitAsync(directory));
        await exit.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref shows));
    }

    [Fact]
    public async Task DifferentProfilesHaveIndependentInstancesAndDisposalAllowsRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-ipc-" + Guid.NewGuid().ToString("N"));
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
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-ipc-" + Guid.NewGuid().ToString("N"));
        var first = new SingleInstanceService(directory, () => { });
        await using var second = new SingleInstanceService(directory, () => { });
        Assert.False(second.IsPrimary);
        await first.DisposeAsync();
        await second.NotifyPrimaryAsync();
        Assert.True(second.IsPrimary);
    }
}
