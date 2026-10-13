using System.IO;
using System.Text;
using System.Text.Json;
using QuickLaunch.Models;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-config-test-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public async Task SnapshotRoundtripCreatesBackupAndOmitsVolatileFields()
    {
        await using var store = ConfigService.Open(_directory);
        var first = store.Initial;
        await store.SaveAsync(first, 0);
        var second = first with { Columns = 4 };
        second.Groups[0].Items[0] = second.Groups[0].Items[0] with { ErrorMessage = "temporary", LaunchCount = 5 };
        await store.SaveAsync(second, 1);
        string text = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("glyph", text);
        Assert.DoesNotContain("errorMessage", text);
        Assert.Contains("\"kind\": \"App\"", text);
        var loaded = ConfigValidation.Parse(Encoding.UTF8.GetBytes(text));
        Assert.Equal(4, loaded.Columns);
        Assert.Equal(5, loaded.Groups[0].Items[0].LaunchCount);
        Assert.Equal(6, ConfigValidation.Parse(await File.ReadAllBytesAsync(ConfigPath + ".bak")).Columns);
    }

    [Fact]
    public async Task CorruptPrimaryRestoresWithoutOverwritingGoodBackup()
    {
        Directory.CreateDirectory(_directory);
        var original = AppConfig.CreateDefault() with { Columns = 3 };
        byte[] backup = JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig);
        await File.WriteAllBytesAsync(ConfigPath + ".bak", backup);
        await File.WriteAllTextAsync(ConfigPath, "broken primary");
        await using var store = ConfigService.Open(_directory);
        Assert.Equal(3, store.Initial.Columns);
        await store.SaveAsync(store.Initial, 0);
        Assert.Equal(backup, await File.ReadAllBytesAsync(ConfigPath + ".bak"));
        Assert.Equal("broken primary", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory, "config.broken-*.json"))));
    }

    [Fact]
    public async Task BothBrokenFilesAreKeptBeforeDefaultsAreWritten()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ConfigPath, "bad primary");
        await File.WriteAllTextAsync(ConfigPath + ".bak", "bad backup");
        await using var store = ConfigService.Open(_directory);
        Assert.Equal(2, Directory.GetFiles(_directory, "*.broken-*.json").Length);
        await store.SaveAsync(store.Initial, 0);
        Assert.Equal(1, ConfigValidation.Parse(await File.ReadAllBytesAsync(ConfigPath)).Version);
    }

    [Fact]
    public async Task FutureVersionRemainsUnchangedAndReadOnly()
    {
        Directory.CreateDirectory(_directory);
        const string future = "{\"version\":99,\"newData\":\"preserve\"}";
        await File.WriteAllTextAsync(ConfigPath, future);
        await using var store = ConfigService.Open(_directory);
        Assert.True(store.IsReadOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(store.Initial, 1));
        Assert.Equal(future, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task ReadOnlyRecoveryDoesNotRenameFilesOrWrite()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ConfigPath, "broken");
        await using var store = ConfigService.Open(_directory, readOnly: true);
        Assert.True(store.IsReadOnly);
        Assert.Empty(Directory.GetFiles(_directory, "*.broken-*.json"));
        Assert.Equal("broken", await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task SharedProfileHasOneWriter()
    {
        await using var first = ConfigService.Open(_directory);
        await first.SaveAsync(first.Initial, 0);
        await using var second = ConfigService.Open(_directory);
        Assert.False(first.IsReadOnly);
        Assert.True(second.IsReadOnly);
        Assert.Equal(first.Initial.Groups[0].Id, second.Initial.Groups[0].Id);
    }

    private sealed class SlowWriter : IConfigWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Continue { get; } = new(false);
        private int _calls;
        public List<int> Columns { get; } = [];
        public void Write(string path, byte[] bytes)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.SetResult();
                if (!Continue.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release the writer.");
            }
            Columns.Add(ConfigValidation.Parse(bytes).Columns);
            new AtomicConfigWriter().Write(path, bytes);
        }
    }
    [Fact]
    public async Task SlowWriteAndQueuedRevisionsKeepNewestSnapshot()
    {
        var writer = new SlowWriter();
        await using var store = ConfigService.Open(_directory, writer: writer);
        var initial = store.SaveAsync(store.Initial, 0);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var newest = store.SaveAsync(store.Initial with { Columns = 4 }, 3);
        var stale = store.SaveAsync(store.Initial with { Columns = 2 }, 2);
        writer.Continue.Set();
        await Task.WhenAll(initial, newest, stale);
        Assert.Equal(3, store.SavedRevision);
        Assert.Equal(4, ConfigValidation.Parse(await File.ReadAllBytesAsync(ConfigPath)).Columns);
        Assert.Equal(new[] { 6, 4 }, writer.Columns);
        writer.Continue.Dispose();
    }

    private sealed class FailOnceWriter : IConfigWriter
    {
        public bool Fail { get; set; }
        public void Write(string path, byte[] bytes)
        {
            if (Fail) { Fail = false; throw new IOException("simulated disk full"); }
            new AtomicConfigWriter().Write(path, bytes);
        }
    }
    [Fact]
    public async Task FailureKeepsOldFileAndAllowsSameRevisionRetry()
    {
        var writer = new FailOnceWriter();
        await using var store = ConfigService.Open(_directory, writer: writer);
        await store.SaveAsync(store.Initial, 0);
        writer.Fail = true;
        var snapshot = store.Initial with { Columns = 2 };
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(snapshot, 1));
        Assert.Equal(0, store.SavedRevision);
        Assert.Equal(6, ConfigValidation.Parse(await File.ReadAllBytesAsync(ConfigPath)).Columns);
        await store.SaveAsync(snapshot, 1);
        Assert.Equal(2, ConfigValidation.Parse(await File.ReadAllBytesAsync(ConfigPath)).Columns);
    }

    [Fact]
    public async Task DisposalDrainsLastQueuedSnapshotAndReleasesLease()
    {
        var store = ConfigService.Open(_directory);
        var pending = store.SaveAsync(store.Initial with { Columns = 8 }, 1);
        await store.DisposeAsync();
        await pending;
        await using var reopened = ConfigService.Open(_directory);
        Assert.False(reopened.IsReadOnly);
        Assert.Equal(8, reopened.Initial.Columns);
    }

    [Fact]
    public void DuplicateIdsAndInvalidLayoutAreRejected()
    {
        var config = AppConfig.CreateDefault();
        config.Groups[0].Items.Add(config.Groups[0].Items[0]);
        Assert.Throws<InvalidDataException>(() => ConfigValidation.Normalize(config));
        Assert.Throws<InvalidDataException>(() => ConfigValidation.Normalize(AppConfig.CreateDefault() with { Columns = 0 }));
        Assert.Throws<InvalidDataException>(() => ConfigValidation.Parse(Encoding.UTF8.GetBytes("{\"version\":\"bad\"}")));
    }

    [Fact]
    public void EmptyGroupsAndStaleSelectionHaveSafeFallback()
    {
        var config = ConfigValidation.Normalize(new AppConfig { LastGroupId = "missing" });
        Assert.Single(config.Groups);
        Assert.Equal(config.Groups[0].Id, config.LastGroupId);
    }

    [Fact]
    public void MissingIdsAndUnknownFieldsCannotBeSilentlyRewritten()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(AppConfig.CreateDefault(), ConfigJsonContext.Default.AppConfig))!;
        json["groups"]![0]!["items"]![0]!.AsObject().Remove("id");
        Assert.Throws<JsonException>(() => ConfigValidation.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Throws<JsonException>(() => ConfigValidation.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"groups\":[],\"unknown\":true}")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        // Tests only create files directly inside this unique directory; no recursive deletion.
        foreach (string file in Directory.GetFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
