using System.Windows;
using System.Windows.Media;
using Swiftdock.Models;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public sealed class ItemEditorValidationTests
{
    [Theory]
    [InlineData("https://example.com/download.exe", false, false, LaunchKind.Url)]
    [InlineData("http://example.com/", true, true, LaunchKind.Url)]
    [InlineData(@"C:\folder.exe", false, true, LaunchKind.Folder)]
    [InlineData(@"C:\app.EXE", true, false, LaunchKind.App)]
    [InlineData(@"C:\app.LnK", true, false, LaunchKind.App)]
    [InlineData(@"C:\app.bat", true, false, LaunchKind.App)]
    [InlineData(@"C:\app.cmd", true, false, LaunchKind.App)]
    [InlineData(@"C:\note.txt", true, false, LaunchKind.File)]
    [InlineData(@"C:\missing.exe", false, false, null)]
    [InlineData("https://", false, false, null)]
    [InlineData("", false, false, null)]
    public void InferenceUsesOnlyUnambiguousFacts(string path, bool file, bool directory, LaunchKind? expected)
        => Assert.Equal(expected, ItemEditorValidation.InferKind(path, file, directory));

    [Theory]
    [InlineData("https://GitHub.com/owner/repo", "github.com")]
    [InlineData("http://localhost:8080/test", "localhost")]
    [InlineData(" \"https://example.com/A?q=B\" ", "example.com")]
    [InlineData("https://", null)]
    [InlineData("github.com", null)]
    [InlineData("ftp://example.com", null)]
    [InlineData("file:///C:/test", null)]
    [InlineData(@"C:\app.exe", null)]
    public void UrlNameIsTheValidWebHost(string text, string? expected)
        => Assert.Equal(expected, ItemEditorValidation.InferUrlName(text));

    [Theory]
    [InlineData(LaunchKind.App, @"C:\missing.exe", false)]
    [InlineData(LaunchKind.App, @"C:\missing.LNK", false)]
    [InlineData(LaunchKind.App, @"C:\missing.bat", false)]
    [InlineData(LaunchKind.App, @"C:\missing.CMD", false)]
    [InlineData(LaunchKind.File, @"C:\missing.txt", false)]
    [InlineData(LaunchKind.File, @"C:\app.exe", false)]
    [InlineData(LaunchKind.Folder, @"C:\folder", true)]
    [InlineData(LaunchKind.Url, "https://example.com/file.exe", false)]
    public void SaveKeepsTheSelectedKind(LaunchKind kind, string path, bool directory)
        => Assert.Equal(kind, ItemEditorValidation.Validate(kind, path, directory));

    [Theory]
    [InlineData(LaunchKind.App, @"C:\note.txt", false, "请选择 .exe、.lnk、.bat 或 .cmd 程序。")]
    [InlineData(LaunchKind.App, @"C:\folder.exe", true, "请选择 .exe、.lnk、.bat 或 .cmd 程序。")]
    [InlineData(LaunchKind.File, @"C:\folder", true, "请选择文件，不能选择文件夹。")]
    [InlineData(LaunchKind.Folder, @"C:\missing", false, "找不到该文件夹。")]
    [InlineData(LaunchKind.Url, "ftp://example.com", false, "请输入有效的 http/https 网址。")]
    [InlineData(LaunchKind.Url, @"C:\app.exe", false, "请输入有效的 http/https 网址。")]
    [InlineData(LaunchKind.File, "https://example.com", false, "请输入不含引号的绝对路径。")]
    [InlineData(LaunchKind.App, "app.exe", false, "请输入不含引号的绝对路径。")]
    [InlineData(LaunchKind.File, "C:\\bad\"name.txt", false, "请输入不含引号的绝对路径。")]
    [InlineData(LaunchKind.Folder, @"C:relative", true, "请输入不含引号的绝对路径。")]
    public void SaveRejectsInvalidTargetsWithShortMessages(LaunchKind kind, string path, bool directory, string expected)
        => Assert.Equal(expected, Assert.Throws<ArgumentException>(() => ItemEditorValidation.Validate(kind, path, directory)).Message);

    [Fact]
    public void QuotesAreRemovedOnlyAtTheTargetBoundary()
    {
        Assert.Equal(@"C:\Program Files\app.exe", ItemEditorValidation.NormalizeTarget(" \"C:\\Program Files\\app.exe\" "));
        Assert.Contains('"', ItemEditorValidation.NormalizeTarget("C:\\bad\"name.exe"));
    }

    [Theory]
    [InlineData("relative")] [InlineData("\"C:\\work\"")] [InlineData("C:\\bad\"work")]
    public void ApplicationsValidateWorkingDirectoryButShortcutsIgnoreIt(string working)
    {
        Assert.Equal("工作目录必须是有效绝对路径。", Assert.Throws<ArgumentException>(() =>
            ItemEditorValidation.Validate(LaunchKind.App, @"C:\app.exe", false, working)).Message);
        Assert.Equal(LaunchKind.App, ItemEditorValidation.Validate(LaunchKind.App, @"C:\app.lnk", false, working));
        Assert.Equal(LaunchKind.File, ItemEditorValidation.Validate(LaunchKind.File, @"C:\file.txt", false, working));
    }

    [Theory]
    [InlineData("")] [InlineData(@"C:\work")] [InlineData(@"\\server\share\work")]
    public void ApplicationsAcceptEmptyOrAbsoluteWorkingDirectories(string working)
        => Assert.Equal(LaunchKind.App, ItemEditorValidation.Validate(LaunchKind.App, @"C:\app.exe", false, working));

    [Theory]
    [InlineData("Segoe Fluent Icons")] [InlineData("Segoe MDL2 Assets")]
    public void EveryTypeAndChevronGlyphExistsInBothIconFonts(string font)
    {
        var typeface = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Assert.True(typeface.TryGetGlyphTypeface(out var glyphs));
        Assert.Contains(font, glyphs.FamilyNames.Values);
        foreach (int codePoint in new[] { 0xE71D, 0xE8A5, 0xE8B7, 0xE774, 0xE76C, 0xE70D, 0xECC8 })
            Assert.True(glyphs.CharacterToGlyphMap.TryGetValue(codePoint, out ushort glyph) && glyph != 0, $"{font}: U+{codePoint:X4}");
    }
}
