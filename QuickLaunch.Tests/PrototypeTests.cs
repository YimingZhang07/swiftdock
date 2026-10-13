using System.Buffers.Binary;
using System.IO;
using System.Text;
using QuickLaunch.DragDrop;
using QuickLaunch.Models;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public class PrototypeTests
{
    [Fact]
    public void EveryInsertionSlotMatchesRemoveThenInsert()
    {
        for (int count = 1; count <= 20; count++)
        for (int source = 0; source < count; source++)
        for (int slot = 0; slot <= count; slot++)
        {
            var expected = Enumerable.Range(0, count).ToList();
            expected.Insert(slot, source);
            expected.RemoveAt(source < slot ? source : source + 1);
            var actual = Enumerable.Range(0, count).ToList();
            actual.RemoveAt(source);
            actual.Insert(ReorderMath.FinalIndex(source, slot, count), source);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(0, 3, 3, 2)] [InlineData(2, 0, 3, 0)] [InlineData(0, 1, 1, 0)]
    public void MoveBoundaries(int source, int slot, int count, int expected) => Assert.Equal(expected, ReorderMath.FinalIndex(source, slot, count));

    [Theory]
    [InlineData(1, 1, 6, 0, 0)] [InlineData(45, 1, 6, 10, 1)]
    [InlineData(1, 90, 6, 10, 6)] [InlineData(450, 450, 6, 10, 10)]
    public void GridSlots(double x, double y, int columns, int count, int expected) => Assert.Equal(expected, ReorderMath.Slot(x, y, columns, count));

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
        string temp = Path.Combine(Path.GetTempPath(), "QuickLaunch-test-" + Guid.NewGuid().ToString("N"));
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
