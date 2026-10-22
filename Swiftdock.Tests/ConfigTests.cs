using System.IO;
using System.Text;
using System.Text.Json;
using Swiftdock.Models;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public sealed class ConfigTests : IDisposable
{
    [Theory]
    [InlineData(null, null, 100)] [InlineData(null, 32, 80)] [InlineData(null, 40, 100)] [InlineData(null, 48, 120)]
    [InlineData(100, 48, 100)] [InlineData(150, 32, 150)]
    [InlineData(int.MinValue, null, 80)] [InlineData(84, null, 80)] [InlineData(85, null, 90)]
    [InlineData(104, null, 100)] [InlineData(105, null, 110)] [InlineData(int.MaxValue, null, 150)]
    public void ScaleNormalizesMigratesAndRoundtripsWithDefaultsOmitted(int? scale, int? legacyIcon, int expected)
    {
        var original = AppConfig.CreateDefault() with { Scale = scale, IconSize = legacyIcon };
        var config = ConfigValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig));
        Assert.Equal(expected, config.Scale ?? 100); Assert.Null(config.IconSize);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.AppConfig);
        using var json = JsonDocument.Parse(bytes);
        Assert.False(json.RootElement.TryGetProperty("iconSize", out _));
        Assert.Equal(expected != 100, json.RootElement.TryGetProperty("scale", out var stored));
        if (expected != 100) Assert.Equal(expected, stored.GetInt32());
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(ConfigValidation.Parse(bytes), ConfigJsonContext.Default.AppConfig));
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Swiftdock-config-test-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Theory]
    [InlineData(null)] [InlineData(true)] [InlineData(false)]
    public void AlwaysOnTopRoundtripOmitsDefaultAndPersistsOptOut(bool? value)
    {
        var original = AppConfig.CreateDefault() with { AlwaysOnTop = value };
        var loaded = ConfigValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig));
        Assert.Equal(value ?? true, loaded.AlwaysOnTop ?? true);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(loaded, ConfigJsonContext.Default.AppConfig);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(value == false, json.RootElement.TryGetProperty("alwaysOnTop", out var stored));
        if (value == false) Assert.False(stored.GetBoolean());
        Assert.Equal(loaded.AlwaysOnTop, ConfigValidation.Parse(bytes).AlwaysOnTop);
    }

    [Fact]
    public void ConfigWithoutWindowSizeRoundtripsUnchanged()
    {
        var original = AppConfig.CreateDefault();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig);
        string json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("windowWidth", json);
        Assert.DoesNotContain("windowHeight", json);
        Assert.DoesNotContain("windowLeft", json);
        Assert.DoesNotContain("windowTop", json);
        Assert.DoesNotContain("placement", json);
        var loaded = ConfigValidation.Parse(bytes);
        Assert.Null(loaded.WindowWidth); Assert.Null(loaded.WindowHeight);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(loaded, ConfigJsonContext.Default.AppConfig));
    }

    [Theory]
    [InlineData(-1920, 0, null)]
    [InlineData(100000, -100000, "cursor")]
    [InlineData(null, null, "cursor")]
    public void PositionAndPlacementRoundtrip(int? left, int? top, string? placement)
    {
        var original = AppConfig.CreateDefault() with { WindowLeft = left, WindowTop = top, Placement = placement };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig);
        var loaded = ConfigValidation.Parse(bytes);
        Assert.Equal(left, loaded.WindowLeft); Assert.Equal(top, loaded.WindowTop); Assert.Equal(placement, loaded.Placement);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(loaded, ConfigJsonContext.Default.AppConfig));
    }

    [Theory]
    [InlineData(null, 10)] [InlineData(10, null)]
    [InlineData(-100001, 0)] [InlineData(100001, 0)]
    [InlineData(0, -100001)] [InlineData(0, 100001)]
    public void InvalidPositionClearsBothCoordinates(int? left, int? top)
    {
        var original = AppConfig.CreateDefault() with { WindowLeft = left, WindowTop = top, Placement = "cursor", WindowWidth = 600, WindowHeight = 400 };
        var expected = original with { WindowLeft = null, WindowTop = null };
        Assert.Equal(expected, ConfigValidation.Normalize(original));
        var loaded = ConfigValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig));
        Assert.Equal(JsonSerializer.Serialize(expected, ConfigJsonContext.Default.AppConfig), JsonSerializer.Serialize(loaded, ConfigJsonContext.Default.AppConfig));
    }

    [Theory]
    [InlineData("lastPosition")] [InlineData("unknown")] [InlineData("")] [InlineData("Cursor")]
    public void DefaultOrUnknownPlacementNormalizesToOmittedDefault(string placement)
    {
        var original = AppConfig.CreateDefault() with { Placement = placement, WindowLeft = -200, WindowTop = 0 };
        var loaded = ConfigValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig));
        Assert.Null(loaded.Placement);
        Assert.Equal(-200, loaded.WindowLeft); Assert.Equal(0, loaded.WindowTop);
        Assert.DoesNotContain("placement", JsonSerializer.Serialize(loaded, ConfigJsonContext.Default.AppConfig));
    }

    [Fact]
    public void OldConfigMigratesIconSize()
    {
        const string json = """
            {
              "version": 1,
              "hotkey": {
                "modifiers": [
                  "Alt"
                ],
                "key": "Space"
              },
              "iconSize": 32,
              "columns": 6,
              "lastGroupId": "0123456789abcdef0123456789abcdef",
              "groups": [
                {
                  "id": "0123456789abcdef0123456789abcdef",
                  "name": "Apps",
                  "items": []
                }
              ]
            }
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(json.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        var migrated = ConfigValidation.Parse(bytes);
        Assert.Equal(80, migrated.Scale); Assert.Null(migrated.IconSize);
        Assert.DoesNotContain("iconSize", JsonSerializer.Serialize(migrated, ConfigJsonContext.Default.AppConfig));
    }

    [Fact]
    public void ConfigWithWindowSizeRoundtrips()
    {
        var original = AppConfig.CreateDefault() with { WindowWidth = 725.5, WindowHeight = 480.25 };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig);
        var loaded = ConfigValidation.Parse(bytes);
        Assert.Equal(original.WindowWidth, loaded.WindowWidth);
        Assert.Equal(original.WindowHeight, loaded.WindowHeight);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(loaded, ConfigJsonContext.Default.AppConfig));
    }

    [Theory]
    [InlineData(null, 400d)]
    [InlineData(600d, null)]
    [InlineData(0d, 400d)]
    [InlineData(600d, -1d)]
    [InlineData(10001d, 400d)]
    [InlineData(600d, 10001d)]
    [InlineData(double.NaN, 400d)]
    [InlineData(600d, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 400d)]
    public void InvalidWindowSizeClearsBothDimensionsAndPreservesConfig(double? width, double? height)
    {
        var original = AppConfig.CreateDefault() with { Columns = 4, Scale = 120, WindowWidth = width, WindowHeight = height };
        var normalized = ConfigValidation.Normalize(original);
        Assert.Equal(original with { WindowWidth = null, WindowHeight = null }, normalized);
        string json = JsonSerializer.Serialize(normalized, ConfigJsonContext.Default.AppConfig);
        Assert.DoesNotContain("windowWidth", json); Assert.DoesNotContain("windowHeight", json);
        if ((!width.HasValue || double.IsFinite(width.Value)) && (!height.HasValue || double.IsFinite(height.Value)))
        {
            var loaded = ConfigValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(original, ConfigJsonContext.Default.AppConfig));
            Assert.Equal(json, JsonSerializer.Serialize(loaded, ConfigJsonContext.Default.AppConfig));
        }
    }

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
    public async Task LegacyAutoHideSettingIsRetiredWithoutLosingItems()
    {
        Directory.CreateDirectory(_directory);
        var original = AppConfig.CreateDefault();
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig))!;
        json["hideOnDeactivate"] = true;
        await File.WriteAllTextAsync(ConfigPath, json.ToJsonString());

        await using var store = ConfigService.Open(_directory);
        Assert.False(store.IsReadOnly);
        Assert.False(store.Initial.HideOnDeactivate);
        Assert.Equal(original.Groups.SelectMany(g => g.Items).Select(i => i.Id),
            store.Initial.Groups.SelectMany(g => g.Items).Select(i => i.Id));
        await store.SaveAsync(store.Initial, 1);
        Assert.DoesNotContain("hideOnDeactivate", await File.ReadAllTextAsync(ConfigPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.broken-*.json"));
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
