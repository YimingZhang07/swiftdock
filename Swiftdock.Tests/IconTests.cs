using System.IO;
using Swiftdock.Models;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public sealed class IconTests
{
    [Theory]
    [InlineData(48, 1, 48)] [InlineData(48, 1.5, 72)] [InlineData(48, 2, 96)] [InlineData(60, 5, 300)]
    public void CacheUsesPhysicalPixels(double dip, double scale, int expected) => Assert.Equal(expected, IconService.PixelSize(dip, scale));

    [Fact]
    public void SourceIdentitySurvivesRestartAndChangesWithPath()
    {
        var item = new LaunchItem("app", @"C:\one.exe", LaunchKind.App);
        Assert.Equal(IconService.SourceKey(item), IconService.SourceKey(item with { Id = Guid.NewGuid().ToString("N") }));
        Assert.NotEqual(IconService.SourceKey(item), IconService.SourceKey(item with { Path = @"C:\two.exe" }));
    }

    [Theory]
    [InlineData(96)] [InlineData(300)]
    public async Task FrozenIconIsReusedFromMemoryThenDiskAndCorruptionRecovers(int pixels)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using (var log = new TestLogging())
            {
                var item = new LaunchItem("Web", "https://example.com", LaunchKind.Url);
                using (var icons = log.CreateIcons(directory))
                {
                    var image = await icons.GetAsync(item, pixels);
                    Assert.NotNull(image); Assert.True(image.IsFrozen); Assert.Equal(pixels, image.PixelWidth);
                    Assert.Same(image, await icons.GetAsync(item, pixels));
                    Assert.Equal(1, icons.Extractions); Assert.Equal(1, icons.MemoryHits);
                }
                using (var icons = log.CreateIcons(directory))
                {
                    Assert.NotNull(await icons.GetAsync(item, pixels));
                    Assert.Equal(1, icons.DiskHits); Assert.Equal(0, icons.Extractions);
                }
                await File.WriteAllTextAsync(Assert.Single(Directory.GetFiles(directory, "*.png")), "broken PNG");
                using (var icons = log.CreateIcons(directory))
                {
                    Assert.NotNull(await icons.GetAsync(item, pixels));
                    Assert.Equal(1, icons.Extractions);
                }
            }
        }
        finally { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }
}
