using System.Globalization;
using System.IO;
using System.Text.Json;
using Swiftdock.Diagnostics;
using Swiftdock.Models;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public sealed class BrowserTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Browser\\browser.exe\" --profile test \"%1\"")]
    [InlineData("C:\\Program Files\\Browser\\browser.exe --profile test %1")]
    [InlineData("C:\\Program Files\\Browser\\browser.exe")]
    [InlineData(" \"%BROWSERROOT%\\browser.exe\" --flag ")]
    [InlineData("%BROWSERROOT%\\browser.exe --flag")]
    public void CommandsExtractOnlyTheExpandedExecutable(string command) =>
        Assert.Equal(@"C:\Program Files\Browser\browser.exe", BrowserCatalog.ParseCommand(command, Expand));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("\"C:\\broken.exe")]
    [InlineData("browser.exe --flag")] [InlineData("C:\\file.cmd --flag")]
    public void InvalidCommandsAreSkipped(string? command) => Assert.Null(BrowserCatalog.ParseCommand(command, Expand));

    [Fact]
    public void RegistrationsResolveIndirectNamesPreferHkcuDeduplicateAndSkipMissingPrograms()
    {
        BrowserRegistration[] entries = [
            new(false, "Machine", null, @"C:\Browsers\same.exe --machine"),
            new(false, "Zulu", null, @"C:\Browsers\z.exe"),
            new(true, "User fallback", "@%BROWSERROOT%\\names.dll,-1", "\"C:\\Browsers\\SAME.exe\" --user"),
            new(true, "Missing", null, @"C:\Browsers\gone.exe"),
            new(false, "Default fallback", "@unresolved,-2", @"C:\Browsers\fallback.exe"),
            new(false, "@names.dll,-3", null, @"C:\Browsers\indirect.exe"),
            new(false, "32-bit duplicate", null, @"c:\browsers\z.exe") ];
        var resolved = new List<string>();
        var result = BrowserCatalog.Parse(entries, p => !p.EndsWith("gone.exe"), value =>
        {
            resolved.Add(value);
            return value.EndsWith(",-1") ? "Alpha user" : value.EndsWith(",-3") ? "Indirect default" : null;
        }, Expand);
        Assert.Equal(["Alpha user", "Default fallback", "Indirect default", "Zulu"], result.Select(b => b.Name));
        Assert.Equal(@"C:\Browsers\SAME.exe", result[0].Path);
        Assert.Contains(@"@C:\Program Files\Browser\names.dll,-1", resolved);
    }

    [Fact]
    public void RegistryReadsAreBehindTheCatalogSeam()
    {
        string exe = Environment.ProcessPath!;
        var catalog = new BrowserCatalog(new RegistryFixture([new(true, "Fixture", null, $"\"{exe}\" --ignored")]));
        Assert.Equal(new BrowserProgram("Fixture", exe), Assert.Single(catalog.Detect()));
    }

    [Theory]
    [InlineData(null, null)] [InlineData(" ", null)] [InlineData("invalid", null)]
    [InlineData("C:\\browser.cmd", null)]
    [InlineData(" %USERPROFILE%\\Custom\\..\\Browser\\custom.exe ", "%USERPROFILE%\\Browser\\custom.exe")]
    public void ConfigNormalizesAndRoundTripsWithoutRequiringAnInstalledProgram(string? path, string? expected)
    {
        var config = ConfigValidation.Normalize(AppConfig.CreateDefault() with { Browser = path });
        Assert.Equal(expected, config.Browser);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.AppConfig);
        Assert.Equal(expected, ConfigValidation.Parse(bytes).Browser);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(expected is not null, document.RootElement.TryGetProperty("browser", out _));
        string full = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Browser", "custom.exe");
        Assert.Equal("%USERPROFILE%\\Browser\\custom.exe", BrowserCatalog.NormalizePath(full));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("%USERPROFILE%\\Browser\\custom.exe", true, false)]
    [InlineData("%USERPROFILE%\\Browser\\custom.exe", false, true)]
    public void LaunchDecisionUsesOneLiteralUrlArgumentOrShellFallback(string? browser, bool present, bool unavailable)
    {
        const string url = "https://example.com/a path?q=\"quoted\"&other=%TEMP%";
        var plan = LaunchService.Plan(new("web", url, LaunchKind.Url), browser: browser, exists: _ => present);
        var info = plan.StartInfo;
        Assert.Equal(unavailable, plan.BrowserUnavailable);
        Assert.Equal(browser is null || !present, info.UseShellExecute);
        Assert.Empty(info.Arguments); Assert.Empty(info.Verb);
        if (info.UseShellExecute)
        {
            Assert.Equal(url, info.FileName); Assert.Empty(info.ArgumentList);
        }
        else
        {
            Assert.Equal(Environment.ExpandEnvironmentVariables(browser!), info.FileName);
            Assert.Equal(Path.GetDirectoryName(info.FileName), info.WorkingDirectory);
            Assert.Equal(url, Assert.Single(info.ArgumentList));
        }
        Assert.Equal(Localization.Text(unavailable ? "SelectedBrowserUnavailable" : "TheShellAcceptedTheLaunchRequest").Value, plan.SuccessText.Value);
        var app = LaunchService.Plan(new("app", @"C:\app.exe", LaunchKind.App), browser: browser, exists: _ => present);
        Assert.True(app.StartInfo.UseShellExecute); Assert.False(app.BrowserUnavailable);
    }

    [Fact]
    public async Task RecorderUsesTheSameRoutingAndDoesNotStartAProgram()
    {
        string log = Path.GetTempFileName();
        try
        {
            var recorder = new E2eLaunchRecorder(log);
            var item = new LaunchItem("web", "https://example.com/a b?q=\"x\"&y=2", LaunchKind.Url);
            await recorder.RecordAsync(item, false, Environment.ProcessPath);
            await recorder.RecordAsync(item, false);
            var fallback = await recorder.RecordAsync(item, false, @"C:\Missing Browser\browser.exe");
            var lines = File.ReadAllLines(log);
            using var browser = JsonDocument.Parse(lines[0]);
            Assert.Equal(Environment.ProcessPath, browser.RootElement.GetProperty("program").GetString());
            Assert.Equal(item.Path, Assert.Single(browser.RootElement.GetProperty("arguments").EnumerateArray()).GetString());
            foreach (string line in lines.Skip(1))
            {
                using var shell = JsonDocument.Parse(line);
                Assert.Equal("shell", shell.RootElement.GetProperty("program").GetString());
                Assert.Empty(shell.RootElement.GetProperty("arguments").EnumerateArray());
            }
            Assert.Equal(Localization.Text("SelectedBrowserUnavailable").Value, fallback.Message);
            Assert.True(fallback.Notice);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void BrowserResourceKeysExistInBothLanguages()
    {
        foreach (string language in new[] { "en", "zh-CN" })
        {
            var set = Localization.Resources.GetResourceSet(language == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language), true, false)!;
            foreach (string key in new[] { "OpenWebsites", "OpenWebsitesDescription", "SystemDefaultBrowser", "CustomBrowserProgram",
                "ChooseBrowserProgram", "ExecutableProgramsExe", "UnavailableBrowserDescription", "SelectedBrowserUnavailable" })
                Assert.False(string.IsNullOrWhiteSpace(set.GetString(key)), key + "/" + language);
        }
    }

    private static string Expand(string value) => value.Replace("%BROWSERROOT%", @"C:\Program Files\Browser");
    private sealed class RegistryFixture(IEnumerable<BrowserRegistration> registrations) : IBrowserRegistry
    {
        public IEnumerable<BrowserRegistration> Read() => registrations;
    }
}
