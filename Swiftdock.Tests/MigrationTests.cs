using System.IO;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Swiftdock-migration-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "QuickLaunch");
    private string Current => Path.Combine(_root, "Swiftdock");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DefaultProfileCopiesConfigAndOptionalBackupOnlyOnce(bool backup)
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "config.json"), "legacy config");
        if (backup) File.WriteAllText(Path.Combine(Legacy, "config.json.bak"), "legacy backup");
        File.WriteAllText(Path.Combine(Legacy, "unrelated.log"), "leave here");
        var paths = DataPaths.Resolve(null, null, _root) with { ConfigDirectory = Current };
        Assert.True(paths.MigrateLegacyConfig(Legacy));
        Assert.Equal("legacy config", File.ReadAllText(Path.Combine(Current, "config.json")));
        Assert.Equal(backup, File.Exists(Path.Combine(Current, "config.json.bak")));
        if (backup) Assert.Equal("legacy backup", File.ReadAllText(Path.Combine(Current, "config.json.bak")));
        Assert.False(paths.MigrateLegacyConfig(Legacy));
        Assert.Equal(backup ? 3 : 2, Directory.GetFiles(Legacy).Length);
        Assert.Equal(backup ? 2 : 1, Directory.GetFiles(Current).Length);
    }

    [Fact]
    public void ExistingCurrentConfigAndBackupAreNeverOverwritten()
    {
        Directory.CreateDirectory(Legacy); Directory.CreateDirectory(Current);
        foreach (string file in new[] { "config.json", "config.json.bak" })
        {
            File.WriteAllText(Path.Combine(Legacy, file), "legacy");
            File.WriteAllText(Path.Combine(Current, file), "current");
        }
        var paths = DataPaths.Resolve(null, null, _root) with { ConfigDirectory = Current };
        Assert.False(paths.MigrateLegacyConfig(Legacy));
        Assert.All(Directory.GetFiles(Current), file => Assert.Equal("current", File.ReadAllText(file)));
        File.Delete(Path.Combine(Current, "config.json"));
        Assert.True(paths.MigrateLegacyConfig(Legacy));
        Assert.Equal("current", File.ReadAllText(Path.Combine(Current, "config.json.bak")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitAndPortableProfilesNeverMigrate(bool portable)
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "config.json"), "legacy");
        if (portable) Directory.CreateDirectory(Path.Combine(_root, "Data"));
        // Even an explicit --config-dir equal to the default must not migrate.
        var paths = DataPaths.Resolve(portable ? null : DataPaths.DefaultConfigDirectory, null, _root)
            with { ConfigDirectory = Current };
        Assert.False(paths.MigrateLegacyConfig(Legacy));
        Assert.False(Directory.Exists(Current));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(Legacy, "config.json")));
    }

    [Fact]
    public void MissingLegacyConfigDoesNotCreateProfile()
    {
        var paths = DataPaths.Resolve(null, null, _root) with { ConfigDirectory = Current };
        Assert.False(paths.MigrateLegacyConfig(Legacy));
        Assert.False(Directory.Exists(Current));
    }

    [Theory]
    [InlineData("QuickLaunch_old", "\"C:\\Old App\\QuickLaunch.exe\" --silent", true)]
    [InlineData("quicklaunch_old", "C:\\Old\\QUICKLAUNCH.EXE", true)]
    [InlineData("QuickLaunch_old", "C:\\Old\\QuickLaunch.exe --config-dir \"%APPDATA%\\QuickLaunch\" --silent", true)]
    [InlineData("QuickLaunch_old", "C:\\Old\\QuickLaunch.exe --config-dir C:\\Other", false)]
    [InlineData("QuickLaunch_old", "C:\\Old\\Swiftdock.exe --silent", false)]
    [InlineData("Unrelated", "C:\\Old\\QuickLaunch.exe --silent", false)]
    [InlineData("QuickLaunch_old", "C:\\Old\\QuickLaunch.exe --config-dir", false)]
    [InlineData("QuickLaunch_old", "C:\\Old\\QuickLaunch.exe --config-dir relative", false)]
    [InlineData("QuickLaunch_old", "C:\\Old\\QuickLaunch.exe --config-dir C:\\Other --config-dir C:\\Other", false)]
    [InlineData("QuickLaunch_old", "QuickLaunch.exe --silent", false)]
    [InlineData("QuickLaunch_old", null, false)]
    public void AutostartMigratesOnlyLegacyDefaultProfileValues(string name, string? command, bool migrate)
    {
        var values = new Dictionary<string, string?> { [name] = command, ["UnrelatedApp"] = "keep" };
        var writes = new List<string>();
        var service = new StartupService(@"C:\New App\Swiftdock.exe", DataPaths.DefaultConfigDirectory, "Tests",
            key => values.GetValueOrDefault(key), (key, value) =>
            {
                writes.Add(key);
                if (value is null) values.Remove(key); else values[key] = value;
            }, valueNames: () => values.Keys.ToArray());
        Assert.Equal(migrate ? 1 : 0, service.MigrateLegacyDefaultProfile());
        Assert.Equal("keep", values["UnrelatedApp"]);
        Assert.Equal(!migrate, values.ContainsKey(name));
        if (migrate)
        {
            Assert.Equal(service.Command, values[service.ValueName]);
            Assert.StartsWith("Swiftdock_", service.ValueName);
            Assert.Contains(StartupService.Quote(DataPaths.DefaultConfigDirectory), service.Command);
            Assert.Equal([service.ValueName, name], writes);
            Assert.Equal(0, service.MigrateLegacyDefaultProfile());
        }
        else Assert.Empty(writes);
    }

    [Fact]
    public void AutostartWriteFailureKeepsLegacyValue()
    {
        var writes = new List<string>();
        var service = new StartupService(@"C:\Swiftdock.exe", Current, "Tests",
            _ => @"C:\QuickLaunch.exe --silent", (name, _) => { writes.Add(name); throw new IOException("denied"); },
            valueNames: () => ["QuickLaunch_old"]);
        Assert.Throws<IOException>(() => service.MigrateLegacyDefaultProfile());
        Assert.Equal([service.ValueName], writes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
