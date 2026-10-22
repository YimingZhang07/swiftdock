using System.Buffers.Binary;
using System.IO;
using System.Text;
using Swiftdock.DragDrop;
using Swiftdock.Models;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public class PrototypeTests
{
    [Fact]
    public void MalformedStreamsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => DropParser.DecodeUrl([1], true));
        Assert.Throws<InvalidDataException>(() => DropParser.DecodeUrl([200], false));
        Assert.Throws<InvalidDataException>(() => DropParser.DecodeUrl(new byte[DropParser.MaxPayloadBytes + 2], true));
        Assert.Null(DropParser.DescriptorTitle([1, 0, 0, 0]));
        Assert.Equal("https://example.com/A", DropParser.DecodeUrl(Encoding.Unicode.GetBytes("\uFEFFhttps://example.com/A\0padding"), true));
    }

    [Fact]
    public void DescriptorNameIsOptionalAndValidated()
    {
        var bytes = new byte[596];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        Encoding.Unicode.GetBytes("页面标题.url\0").CopyTo(bytes, 76);
        Assert.Equal("页面标题", DropParser.DescriptorTitle(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1000);
        Assert.Null(DropParser.DescriptorTitle(bytes));
    }

    [Theory]
    [InlineData("https://example.com/A?q=X", true)] [InlineData("file:///C:/a.exe", false)]
    [InlineData("javascript:alert(1)", false)] [InlineData("https://", false)]
    public void OnlyAbsoluteWebUrls(string text, bool allowed) => Assert.Equal(allowed, LaunchService.IsWebUrl(text));

    [Fact]
    public async Task BrowserWithoutTitleFallsBackToHostAndPreservesCase()
    {
        var result = await DropParser.ResolveAsync(new([], [], "https://example.com/A?q=X", null), default);
        Assert.Empty(result.Errors);
        var item = Assert.Single(result.Items);
        Assert.Equal("example.com", item.Name);
        Assert.Equal("https://example.com/A?q=X", item.Path);
    }

    [Fact]
    public void ShortcutKeepsOwnArgumentsAndWorkingDirectory()
    {
        var info = LaunchService.CreateStartInfo(new("test", @"C:\app.lnk", LaunchKind.App, "ignored", @"C:\ignored"));
        Assert.True(info.UseShellExecute);
        Assert.Empty(info.Arguments);
        Assert.Empty(info.WorkingDirectory);
    }

    [Fact]
    public void EnvironmentExpansionNeverTouchesUrl()
    {
        var info = LaunchService.CreateStartInfo(new("test", "https://example.com/%TEMP%", LaunchKind.Url));
        Assert.Equal("https://example.com/%TEMP%", info.FileName);
    }

    [Fact]
    public async Task MixedFilesKeepInputOrderAndRejectUnsafeInternetShortcut()
    {
        string temp = Path.Combine(Path.GetTempPath(), "Swiftdock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string good = Path.Combine(temp, "good.url"), bad = Path.Combine(temp, "bad.url"), file = Path.Combine(temp, "note.txt");
            await File.WriteAllTextAsync(good, "[InternetShortcut]\nURL=https://example.com/A\n");
            await File.WriteAllTextAsync(bad, "[InternetShortcut]\nURL=file:///C:/app.exe\n");
            await File.WriteAllTextAsync(file, "fixture");
            var result = await DropParser.ResolveAsync(new([], [file, good, bad, temp], null, null), default);
            Assert.Equal(new[] { LaunchKind.File, LaunchKind.Url, LaunchKind.Folder }, result.Items.Select(i => i.Kind));
            Assert.Single(result.Errors);
        }
        finally
        {
            foreach (var name in new[] { "good.url", "bad.url", "note.txt" }) File.Delete(Path.Combine(temp, name));
            Directory.Delete(temp);
        }
    }
}
