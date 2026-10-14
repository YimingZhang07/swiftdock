using System.IO;
using System.Windows;
using Microsoft.Win32;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class InteractionTests
{
    [Fact]
    public async Task CustomIconSurvivesEditRenameMovesDeleteUndoAndPersistence() => await OnSta(vm =>
    {
        var group = vm.Groups[0]; var original = group.Items[0];
        vm.UpdateItem(group, original, original with { Icon = "glyph:E943:#2F6FDB" });
        var item = group.Items[0]; Assert.NotSame(original.Visual, item.Visual);
        Assert.True(vm.IsDuplicate(group, item with { Icon = "glyph:E735:#0F7B6C" }));
        vm.UpdateItem(group, item, item with { Name = "renamed", Arguments = "--test" });
        Assert.Same(item.Visual, group.Items[0].Visual); item = group.Items[0];
        Assert.True(vm.MoveItem(group, group, item, group.Items.Count));
        Assert.True(vm.MoveItem(group, vm.Groups[1], item, 0));
        var target = vm.MoveToNewGroup(vm.Groups[1], item, "custom")!;
        vm.DeleteItem(target, target.Items[0]); Assert.True(vm.UndoDelete());
        Assert.Equal(item.Icon, target.Items[0].Icon); Assert.Equal(item.Id, target.Items[0].Id);
        var loaded = ConfigValidation.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(vm.Snapshot(), ConfigJsonContext.Default.AppConfig));
        Assert.Equal(item.Icon, loaded.Groups.Single(g => g.Id == target.Id).Items[0].Icon);
        vm.UpdateItem(target, target.Items[0], target.Items[0] with { Icon = null });
        Assert.Null(target.Items[0].Icon); Assert.NotSame(item.Visual, target.Items[0].Visual);
    });

    [Theory]
    [InlineData(1, true)] [InlineData(2, false)] [InlineData(3, false)]
    public void HeaderMoveHandlesClicksWithoutMovingOnDoubleClick(int clickCount, bool move) =>
        Assert.Equal((true, move), LauncherWindow.WindowDragDecision(clickCount, false, false, false, false, false));

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, true, true, true, true)]
    public void HeaderMoveGuardsIgnoreMouseDown(bool closing, bool movingWindow, bool sizeMove, bool dragActive, bool externalDrag)
    {
        foreach (int clicks in new[] { 1, 2 })
            Assert.Equal((false, false), LauncherWindow.WindowDragDecision(clicks, closing, movingWindow, sizeMove, dragActive, externalDrag));
    }

    [Theory]
    [InlineData(32, 1, 40)] [InlineData(40, 1, 48)] [InlineData(48, 1, 48)]
    [InlineData(48, -1, 40)] [InlineData(40, -1, 32)] [InlineData(32, -1, 32)]
    [InlineData(36, 1, 40)] [InlineData(36, -1, 32)] [InlineData(44, 1, 48)] [InlineData(44, -1, 40)]
    [InlineData(16, 1, 32)] [InlineData(16, -1, 32)] [InlineData(128, 1, 48)] [InlineData(128, -1, 48)]
    [InlineData(36, 0, 36)]
    public void IconSizeStepsToNextPreset(int size, int direction, int expected) => Assert.Equal(expected, IconSizePresets.Step(size, direction));

    [Theory]
    [InlineData(16)] [InlineData(32)] [InlineData(36)] [InlineData(40)] [InlineData(48)] [InlineData(128)]
    public async Task SetIconSizeNotifiesAndUsesSavePipeline(int size)
    {
        var writer = new RecordingWriter();
        await OnSta(vm =>
        {
            var notifications = new List<string?>();
            vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            long revision = vm.Revision;
            vm.SetIconSize(size);
            Assert.Equal(size, vm.IconSize); Assert.Equal(size, vm.Snapshot().IconSize);
            Assert.Equal(Math.Max(84, size + 52), vm.CellSize); Assert.Equal(vm.CellSize - 4, vm.TileSize);
            Assert.Equal(revision + 1, vm.Revision);
            Assert.Equal(new[] { "IconSize", "CellSize", "TileSize" }, notifications.Where(n => n != "StorageStatus" && n != "StorageWarning"));
            notifications.Clear(); vm.SetIconSize(size);
            Assert.Empty(notifications); Assert.Equal(revision + 1, vm.Revision);
            vm.FlushAsync().GetAwaiter().GetResult();
            Assert.Equal("已保存", vm.StorageStatus);
            Assert.Equal(size, ConfigValidation.Parse(writer.Bytes!).IconSize);
        }, iconSize: 64, writer: writer);
    }

    [Theory]
    [InlineData(15)] [InlineData(129)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public async Task SetIconSizeRejectsInvalidRange(int size) => await OnSta(vm =>
    {
        long revision = vm.Revision;
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.SetIconSize(size));
        Assert.Equal(32, vm.IconSize); Assert.Equal(revision, vm.Revision);
    });

    [Fact]
    public async Task ReadOnlyIconSizeIsUnchanged() => await OnSta(vm =>
    {
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        long revision = vm.Revision;
        vm.SetIconSize(48); vm.SetIconSize(129);
        Assert.Equal(32, vm.IconSize); Assert.Equal(32, vm.Snapshot().IconSize);
        Assert.Equal(revision, vm.Revision); Assert.Empty(notifications);
        vm.FlushAsync().GetAwaiter().GetResult();
    }, readOnly: true);

    [Theory]
    [InlineData(16, 84, 80)] [InlineData(32, 84, 80)] [InlineData(40, 92, 88)]
    [InlineData(48, 100, 96)] [InlineData(128, 180, 176)]
    public async Task TileSizingPreservesStoredIconSize(int icon, double cell, double tile) => await OnSta(vm =>
    {
        Assert.Equal(icon, vm.IconSize); Assert.Equal(icon, vm.Snapshot().IconSize);
        Assert.Equal(cell, vm.CellSize); Assert.Equal(tile, vm.TileSize);
    }, iconSize: icon);

    [Fact]
    public async Task NewProfilesUse32DipIconsAndSilentStorageStatus() => await OnSta(vm =>
    {
        Assert.Equal(32, vm.IconSize); Assert.Equal("", vm.StorageWarning);
    });

    [Fact]
    public async Task WindowSizeUsesSavePipelineAndResetClearsBothDimensions() => await OnSta(vm =>
    {
        long revision = vm.Revision;
        vm.SetWindowSize(new(720.5, 500.25));
        Assert.Equal(revision + 1, vm.Revision);
        Assert.Equal(720.5, vm.Snapshot().WindowWidth); Assert.Equal(500.25, vm.Snapshot().WindowHeight);
        vm.SetWindowSize(new(720.5, 500.25));
        Assert.Equal(revision + 1, vm.Revision);
        vm.SetWindowSize(null);
        Assert.Equal(revision + 2, vm.Revision);
        Assert.Null(vm.WindowSize); Assert.Null(vm.Snapshot().WindowWidth); Assert.Null(vm.Snapshot().WindowHeight);
    });

    [Fact]
    public async Task ReadOnlyWindowSizeIsSessionOnly() => await OnSta(vm =>
    {
        long revision = vm.Revision;
        vm.SetWindowSize(new(720, 500));
        Assert.Equal(new Size(720, 500), vm.WindowSize);
        Assert.Equal(revision, vm.Revision);
        vm.FlushAsync().GetAwaiter().GetResult();
    }, readOnly: true);

    [Fact]
    public void UrlIdentityPreservesPathQueryAndFragmentCase()
    {
        LaunchItem Url(string value) => new("web", value, LaunchKind.Url);
        Assert.Equal(ItemIdentity.Key(Url("https://EXAMPLE.com:443/A?q=X#F")), ItemIdentity.Key(Url("https://example.com/A?q=X#F")));
        Assert.NotEqual(ItemIdentity.Key(Url("https://example.com/A")), ItemIdentity.Key(Url("https://example.com/a")));
        Assert.NotEqual(ItemIdentity.Key(Url("https://example.com/?q=X")), ItemIdentity.Key(Url("https://example.com/?q=x")));
        Assert.NotEqual(ItemIdentity.Key(Url("https://User@example.com")), ItemIdentity.Key(Url("https://user@example.com")));
    }
    [Fact]
    public void FileIdentityHandlesEnvironmentVariablesButKeepsArguments()
    {
        var item = new LaunchItem("App", @"C:\Users\test\app.exe", LaunchKind.App);
        Assert.Equal(ItemIdentity.Key(item), ItemIdentity.Key(item with { Path = @"c:\users\TEST\APP.exe" }));
        Assert.NotEqual(ItemIdentity.Key(item), ItemIdentity.Key(item with { Arguments = "--profile=A" }));
        Assert.NotEqual(ItemIdentity.Key(item), ItemIdentity.Key(item with { RunAsAdmin = true }));
        Assert.Equal(@"%USERPROFILE%\file.txt", ItemIdentity.PortablePath(@"C:\Users\me\file.txt", @"C:\Users\me"));
        Assert.Equal(@"C:\Users\me2\file.txt", ItemIdentity.PortablePath(@"C:\Users\me2\file.txt", @"C:\Users\me"));
    }
    [Fact]
    public async Task ImportPlaceholdersNeverPersistAndDuplicatesAreSkipped() => await OnSta(vm =>
    {
        var group = vm.Groups[0]; int before = group.Items.Count;
        var pending = vm.BeginImport(group, 1, 3);
        Assert.Equal(before, vm.Snapshot().Groups[0].Items.Count);
        var item = new LaunchItem("A", "https://example.com/A", LaunchKind.Url);
        var result = vm.CompleteImport(group, pending, [item, item with { Id = Guid.NewGuid().ToString("N") }, item with { Path = "https://example.com/a", Id = Guid.NewGuid().ToString("N") }]);
        Assert.Equal(2, result.Added); Assert.NotNull(result.Duplicate);
        Assert.DoesNotContain(group.Items, i => i.IsPending);
        Assert.Equal(item.Id, group.Items[1].Id);
    });
    [Fact]
    public async Task ImportIntoDeletedGroupDoesNotResurrectIt() => await OnSta(vm =>
    {
        var group = vm.AddGroup("temporary"); var pending = vm.BeginImport(group, 0, 1);
        vm.RemoveGroup(group);
        Assert.Equal(0, vm.CompleteImport(group, pending, [new("web", "https://example.com", LaunchKind.Url)]).Added);
        Assert.DoesNotContain(group, vm.Groups);
    });
    [Fact]
    public async Task CrossGroupConflictPreservesSourceAndNewGroupMoveKeepsId() => await OnSta(vm =>
    {
        var source = vm.Groups[0]; var target = vm.Groups[1]; var item = source.Items[0];
        target.Items.Add(item with { Id = Guid.NewGuid().ToString("N") });
        Assert.False(vm.MoveItem(source, target, item, 0)); Assert.Contains(item, source.Items);
        var created = vm.MoveToNewGroup(source, item, "new"); Assert.NotNull(created);
        Assert.Equal(item.Id, Assert.Single(created.Items).Id); Assert.DoesNotContain(item, source.Items);
    });
    [Fact]
    public async Task EditAndRenamePreserveIdAndDeleteUndoExpires()
    {
        var now = DateTimeOffset.UtcNow;
        await OnSta(vm =>
        {
            var group = vm.Groups[0]; var original = group.Items[0];
            vm.UpdateItem(group, original, original with { Name = "renamed" });
            Assert.Equal(original.Id, group.Items[0].Id);
            vm.DeleteItem(group, group.Items[0]); Assert.True(vm.UndoDelete());
            Assert.Equal(original.Id, group.Items[0].Id);
            vm.DeleteItem(group, group.Items[0]); now += TimeSpan.FromSeconds(6); Assert.False(vm.UndoDelete());
        }, () => now);
    }
    [Fact]
    public async Task LastGroupDeletionHasFallbackAndGroupOrderPersists() => await OnSta(vm =>
    {
        var first = vm.Groups[0]; var second = vm.Groups[1];
        vm.RenameGroup(first, "Renamed"); vm.MoveGroup(0, 1);
        Assert.Equal(second.Id, vm.Snapshot().Groups[0].Id);
        vm.RemoveGroup(first); vm.RemoveGroup(second);
        Assert.Single(vm.Groups); Assert.Equal(vm.Groups[0].Id, vm.LastGroupId);
    });
    [Fact]
    public void StartupRegistryRoundtripTouchesOnlyTestKey()
    {
        string key = @"Software\QuickLaunch\Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var service = new StartupService(@"C:\Program Files\QuickLaunch\QuickLaunch.exe", @"C:\Test Profile", key);
            Assert.False(service.IsEnabled); service.SetEnabled(true); Assert.True(service.IsEnabled);
            Assert.Contains("--silent --config-dir \"C:\\Test Profile\"", service.Command);
            service.SetEnabled(false); Assert.False(service.IsEnabled);
            Assert.Equal("\"C:\\\\\"", StartupService.Quote(@"C:\"));
        }
        finally { Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false); }
    }
    private sealed class RecordingWriter : IConfigWriter
    {
        public byte[]? Bytes { get; private set; }
        public void Write(string path, byte[] bytes) => Bytes = bytes;
    }

    private static Task OnSta(Action<MainViewModel> action, Func<DateTimeOffset>? clock = null, bool readOnly = false, int? iconSize = null, IConfigWriter? writer = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-interaction-" + Guid.NewGuid().ToString("N"));
            ConfigService? store = null;
            try
            {
                if (iconSize is { } icon)
                {
                    Directory.CreateDirectory(directory);
                    new AtomicConfigWriter().Write(Path.Combine(directory, "config.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                        AppConfig.CreateDefault() with { IconSize = icon }, ConfigJsonContext.Default.AppConfig));
                }
                store = ConfigService.Open(directory, readOnly: readOnly, writer: writer); var vm = new MainViewModel(store, clock);
                action(vm);
                if (readOnly) Assert.False(File.Exists(store.FilePath));
                else store.SaveAsync(vm.Snapshot(), vm.Revision).GetAwaiter().GetResult();
                vm.Suspend(true);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
