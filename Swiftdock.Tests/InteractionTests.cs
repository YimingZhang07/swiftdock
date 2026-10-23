using System.IO;
using System.Windows;
using Swiftdock.Models;
using Swiftdock.Services;
using Swiftdock.ViewModels;
using Swiftdock.Views;
using Xunit;

namespace Swiftdock.Tests;

public sealed class InteractionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task BrowserPreferenceUsesTheSavePipelineAndRespectsReadOnly(bool readOnly) => OnSta(vm =>
    {
        long revision = vm.Revision;
        vm.SetBrowser(@" %USERPROFILE%\Browsers\..\Browser\custom.exe ");
        Assert.Equal(readOnly ? null : @"%USERPROFILE%\Browser\custom.exe", vm.Browser);
        Assert.Equal(revision + (readOnly ? 0 : 1), vm.Revision);
        vm.SetBrowser(vm.Browser);
        Assert.Equal(revision + (readOnly ? 0 : 1), vm.Revision);
        var loaded = ConfigValidation.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(vm.Snapshot(), ConfigJsonContext.Default.AppConfig));
        Assert.Equal(vm.Browser, loaded.Browser);
        vm.SetBrowser(null); Assert.Null(vm.Browser);
        Assert.Equal(revision + (readOnly ? 0 : 2), vm.Revision);
    }, readOnly: readOnly);

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void HotkeyHidesVisibleActiveOrPinnedPanel(bool visible, bool active, bool pinned, bool hide) =>
        Assert.Equal(hide ? LauncherWindow.ToggleAction.Hide : LauncherWindow.ToggleAction.Show,
            LauncherWindow.ToggleDecision(visible, active, pinned));

    [Fact]
    public void HotkeyGuardsKeepOwnedDialogsFirstAndIgnoreInteractions()
    {
        foreach (bool visible in new[] { false, true })
        foreach (bool pinned in new[] { false, true })
        foreach (var guard in new[] { (true, false, false, false), (false, true, false, false),
            (false, false, true, false), (false, false, false, true), (true, true, true, true) })
        {
            Assert.Equal(LauncherWindow.ToggleAction.Ignore, LauncherWindow.ToggleDecision(visible, false, pinned,
                false, guard.Item1, guard.Item2, guard.Item3, guard.Item4));
            Assert.Equal(LauncherWindow.ToggleAction.ActivateOwnedDialog, LauncherWindow.ToggleDecision(visible, false, pinned,
                true, guard.Item1, guard.Item2, guard.Item3, guard.Item4));
        }
        Assert.Equal(LauncherWindow.ToggleAction.ActivateOwnedDialog,
            LauncherWindow.ToggleDecision(true, true, true, ownedDialog: true));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task PinUsesSavePipelineAndRespectsCanEdit(bool readOnly) => OnSta(vm =>
    {
        Assert.True(vm.AlwaysOnTop);
        long revision = vm.Revision;
        vm.AlwaysOnTop = false;
        vm.AlwaysOnTop = false;
        Assert.Equal(readOnly, vm.AlwaysOnTop);
        Assert.Equal(readOnly ? null : (bool?)false, vm.Snapshot().AlwaysOnTop);
        Assert.Equal(revision + (readOnly ? 0 : 1), vm.Revision);
        vm.AlwaysOnTop = true;
        Assert.True(vm.AlwaysOnTop); Assert.Null(vm.Snapshot().AlwaysOnTop);
        Assert.Equal(revision + (readOnly ? 0 : 2), vm.Revision);
        vm.Suspend(true); vm.AlwaysOnTop = false;
        Assert.True(vm.AlwaysOnTop);
    }, readOnly: readOnly);

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
    [InlineData(80, 1, 90)] [InlineData(100, 1, 110)] [InlineData(150, 1, 150)]
    [InlineData(150, -1, 140)] [InlineData(100, -1, 90)] [InlineData(80, -1, 80)]
    [InlineData(100, 0, 100)]
    public void ScaleWheelStepsByTen(int scale, int direction, int expected) => Assert.Equal(expected, PanelMetrics.Step(scale, direction));

    [Theory]
    [InlineData(80)] [InlineData(100)] [InlineData(150)]
    public async Task SetScaleNotifiesAndRescalesSavedSizeThroughSavePipeline(int scale)
    {
        var writer = new RecordingWriter();
        await OnSta(vm =>
        {
            vm.SetWindowSize(new(720, 400));
            var notifications = new List<string?>();
            vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            long revision = vm.Revision;
            vm.SetScale(scale);
            Assert.Equal(scale, vm.Scale); Assert.Equal(scale, vm.Snapshot().Scale ?? 100);
            Assert.Equal(40 * scale / 100, vm.IconSize);
            Assert.Equal(92 * scale / 100d, vm.CellSize, 8); Assert.Equal(88 * scale / 100d, vm.TileSize, 8);
            Assert.Equal(720 * scale / 120d, vm.WindowSize!.Value.Width, 8);
            Assert.Equal(400 * scale / 120d, vm.WindowSize.Value.Height, 8);
            Assert.Equal(revision + 1, vm.Revision);
            Assert.Equal(new[] { "Scale", "Metrics", "IconSize", "CellSize", "TileSize" }, notifications.Where(n => n != "StorageStatus" && n != "StorageWarning"));
            notifications.Clear(); vm.SetScale(scale);
            Assert.Empty(notifications); Assert.Equal(revision + 1, vm.Revision);
            vm.FlushAsync().GetAwaiter().GetResult();
            Assert.Equal(scale, ConfigValidation.Parse(writer.Bytes!).Scale ?? 100);
        }, scale: 120, writer: writer);
    }

    [Fact]
    public async Task ReadOnlyScaleIsUnchanged() => await OnSta(vm =>
    {
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        long revision = vm.Revision;
        vm.SetScale(150); vm.SetScale(int.MaxValue);
        Assert.Equal(120, vm.Scale); Assert.Equal(120, vm.Snapshot().Scale);
        Assert.Equal(revision, vm.Revision); Assert.Empty(notifications);
        vm.FlushAsync().GetAwaiter().GetResult();
    }, readOnly: true, legacyIcon: 48);

    [Fact]
    public async Task NewProfilesUse100PercentAndSilentStorageStatus() => await OnSta(vm =>
    {
        Assert.Equal(100, vm.Scale); Assert.Equal(40, vm.IconSize); Assert.Equal("", vm.StorageWarning);
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
        vm.SetWindowPosition(new(-120, 240));
        Assert.Equal(revision + 3, vm.Revision);
        Assert.Equal(-120, vm.Snapshot().WindowLeft); Assert.Equal(240, vm.Snapshot().WindowTop);
        vm.SetWindowPosition(new(-120, 240));
        Assert.Equal(revision + 3, vm.Revision);
        vm.SetWindowPosition(null);
        Assert.Equal(revision + 4, vm.Revision);
        Assert.Null(vm.Snapshot().WindowLeft); Assert.Null(vm.Snapshot().WindowTop);
        vm.SetWindowPosition(null); vm.SetWindowSize(null);
        Assert.Equal(revision + 4, vm.Revision);
    });

    [Fact]
    public async Task ReadOnlyWindowSizeIsSessionOnly() => await OnSta(vm =>
    {
        long revision = vm.Revision;
        vm.SetWindowSize(new(720, 500));
        Assert.Equal(new Size(720, 500), vm.WindowSize);
        vm.SetWindowPosition(new(-120, 240));
        Assert.Equal(new Point(-120, 240), vm.WindowPosition);
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
    public void StartupRegistrationRoundtripUsesProfileKeyAndCommand()
    {
        var values = new Dictionary<string, string>();
        var service = new StartupService(@"C:\Program Files\Swiftdock\Swiftdock.exe", @"C:\Test Profile", @"Software\Swiftdock\Tests", values.GetValueOrDefault, (name, command) =>
        {
            if (command is null) values.Remove(name); else values[name] = command;
        });
        Assert.False(service.IsEnabled); service.SetEnabled(true); Assert.True(service.IsEnabled);
        Assert.Equal(service.Command, values[service.ValueName]);
        Assert.StartsWith("Swiftdock_", service.ValueName);
        Assert.Contains("--silent --config-dir \"C:\\Test Profile\"", service.Command);
        service.SetEnabled(false); Assert.False(service.IsEnabled); Assert.Empty(values);
        Assert.Equal("\"C:\\\\\"", StartupService.Quote(@"C:\"));
    }
    private sealed class RecordingWriter : IConfigWriter
    {
        public byte[]? Bytes { get; private set; }
        public void Write(string path, byte[] bytes) => Bytes = bytes;
    }

    private static Task OnSta(Action<MainViewModel> action, Func<DateTimeOffset>? clock = null, bool readOnly = false,
        int? scale = null, IConfigWriter? writer = null, int? legacyIcon = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-interaction-" + Guid.NewGuid().ToString("N"));
            ConfigService? store = null;
            try
            {
                if (scale is not null || legacyIcon is not null)
                {
                    Directory.CreateDirectory(directory);
                    new AtomicConfigWriter().Write(Path.Combine(directory, "config.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                        AppConfig.CreateDefault() with { Scale = scale, IconSize = legacyIcon }, ConfigJsonContext.Default.AppConfig));
                }
                store = ConfigService.Open(directory, readOnly: readOnly, writer: writer); var vm = new MainViewModel(store, clock);
                byte[]? original = File.Exists(store.FilePath) ? File.ReadAllBytes(store.FilePath) : null;
                action(vm);
                if (readOnly)
                {
                    if (original is null) Assert.False(File.Exists(store.FilePath));
                    else Assert.Equal(original, File.ReadAllBytes(store.FilePath));
                }
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
