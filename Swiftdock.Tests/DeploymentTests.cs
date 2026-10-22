using System.IO;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public class DeploymentTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DataDirectorySelectsPortableUnlessConfigIsExplicit(bool hasData, bool explicitConfig)
    {
        string root = Path.Combine(Path.GetTempPath(), "Swiftdock-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string data = Path.Combine(root, "Data"), profile = Path.Combine(root, "profile");
            if (hasData) Directory.CreateDirectory(data);
            var paths = DataPaths.Resolve(explicitConfig ? profile : null, null, root);
            bool portable = hasData && !explicitConfig;
            Assert.Equal(portable, paths.Portable);
            Assert.Equal(explicitConfig ? profile : portable ? data : DataPaths.DefaultConfigDirectory, paths.ConfigDirectory);
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swiftdock");
            Assert.Equal(Path.Combine(portable ? data : local, "logs"), paths.LogDirectory);
            if (portable) Assert.Equal(Path.Combine(data, "icons"), paths.IconDirectory);
            else Assert.StartsWith(Path.Combine(local, "icons") + Path.DirectorySeparatorChar, paths.IconDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true, null, true)] // Single-file self-contained: no loose runtime files.
    [InlineData(false, null, false)] // Repository runtime alone cannot support login startup.
    [InlineData(false, "9.0.1", false)]
    [InlineData(false, "10.0.1", true)]
    public void LoginStartupRequiresSelfContainedOrSystemDesktop10(bool selfContained, string? systemVersion, bool allowed)
    {
        string root = Path.Combine(Path.GetTempPath(), "Swiftdock-startup-" + Guid.NewGuid().ToString("N"));
        string shared = Path.Combine(root, "system", "shared", "Microsoft.WindowsDesktop.App");
        Directory.CreateDirectory(Path.Combine(root, ".tools", "dotnet", "shared", "Microsoft.WindowsDesktop.App", "10.0.1"));
        try
        {
            if (systemVersion is not null) Directory.CreateDirectory(Path.Combine(shared, systemVersion));
            string? registered = null;
            var service = new StartupService(Path.Combine(root, "Swiftdock.exe"), Path.Combine(root, "profile"),
                StartupService.RunKey, _ => registered, (_, command) => registered = command,
                canStartAtLogin: () => StartupService.CanStartAtLogin(selfContained, shared));
            if (allowed) { service.SetEnabled(true); Assert.True(service.IsEnabled); }
            else { Assert.Throws<InvalidOperationException>(() => service.SetEnabled(true)); Assert.Null(registered); }
            service.SetEnabled(false); // Disabling must work even without an available runtime.
            Assert.Null(registered);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PortableLoginStartupRediscoversDataAndKeepsItsOwnProfileKey()
    {
        const string exe = @"C:\Portable\Swiftdock.exe", data = @"C:\Portable\Data";
        var portable = new StartupService(exe, data, "Tests", _ => null, (_, _) => { }, portable: true);
        var explicitProfile = new StartupService(exe, data, "Tests", _ => null, (_, _) => { });
        var installed = new StartupService(@"C:\Programs\Swiftdock\Swiftdock.exe", DataPaths.DefaultConfigDirectory,
            "Tests", _ => null, (_, _) => { });
        Assert.Equal("\"C:\\Portable\\Swiftdock.exe\" --silent", portable.Command);
        Assert.Equal(explicitProfile.ValueName, portable.ValueName);
        Assert.NotEqual(installed.ValueName, portable.ValueName);
    }
}
