using System.IO;
using Microsoft.Win32;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class InteractionTests
{
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
    private static Task OnSta(Action<MainViewModel> action, Func<DateTimeOffset>? clock = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-interaction-" + Guid.NewGuid().ToString("N"));
            ConfigService? store = null;
            try
            {
                store = ConfigService.Open(directory); var vm = new MainViewModel(store, clock);
                action(vm); store.SaveAsync(vm.Snapshot(), vm.Revision).GetAwaiter().GetResult(); vm.Suspend(true);
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
