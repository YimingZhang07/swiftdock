using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLaunch.E2E;
using QuickLaunch.Models;
using QuickLaunch.Services;
using Xunit;
using Xunit.Abstractions;

namespace QuickLaunch.Tests;

[Collection("WPF")]
public sealed class IconImageMatcherTests(ITestOutputHelper output)
{
    [SystemIconsFact]
    public Task ThinColourIconMatchesAtTileSizeButRejectsPlaceholderAndPreviousIcon() => WpfTestHost.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-icon-match-" + Guid.NewGuid().ToString("N"));
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(directory);
        try
        {
            foreach (int size in new[] { 32, 64 })
            {
                string? systemSpec = null;
                foreach (var entry in await icons.GetSystemIconsAsync())
                    if (await icons.GetSystemPickerImageAsync(entry, size * 28 / 32) is not null) { systemSpec = entry.Spec; break; }
                Assert.NotNull(systemSpec);
                async Task<(BitmapSource Image, IconImageMatcher.Raster Reference)> Render(string? spec)
                {
                    var image = await icons.GetAsync(new LaunchItem("URL", "https://example.com/", LaunchKind.Url) { Icon = spec }, size);
                    Assert.NotNull(image);
                    // Read the actual app-written PNG via the same cache-name contract as E2E.
                    using var stream = File.OpenRead(Path.Combine(directory, IconImageMatcher.CacheFileName(spec, size)));
                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    return (image, Pixels(frame));
                }
                var chosen = await Render("fluent:apps_list");
                var system = await Render(systemSpec);
                var placeholder = await Render(null);
                var other = await Render("fluent:apps");
                foreach (var background in new[] { Color.FromRgb(32, 32, 32), Color.FromRgb(243, 243, 243) })
                foreach (var offset in new[] { new Vector(0, 0), new Vector(.5, .5), new Vector(1, -1) })
                {
                    var correct = IconImageMatcher.Compare(chosen.Reference, Capture(chosen.Image, size, background, offset));
                    output.WriteLine($"{size}px/{background}/{offset}: {correct}");
                    Assert.True(correct.Matches, correct.ToString());
                    Assert.True(IconImageMatcher.Compare(system.Reference, Capture(system.Image, size, background, offset)).Matches);
                    foreach (var wrong in new[] { placeholder.Image, system.Image, other.Image, null })
                    {
                        var result = IconImageMatcher.Compare(chosen.Reference, Capture(wrong, size, background, offset));
                        output.WriteLine($"  wrong/absent: {result}");
                        Assert.False(result.Matches, $"Accepted wrong/absent icon at {size}px/{background}: {result}");
                    }
                    Assert.False(IconImageMatcher.Compare(system.Reference, Capture(placeholder.Image, size, background, offset)).Matches);
                }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    private static IconImageMatcher.Raster Capture(BitmapSource? image, int size, Color background, Vector offset)
    {
        int padding = IconImageMatcher.Padding;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size + 2 * padding, size + 2 * padding));
            if (image is not null) drawing.DrawImage(image, new Rect(padding + offset.X, padding + offset.Y, size, size));
        }
        var bitmap = new RenderTargetBitmap(size + 2 * padding, size + 2 * padding, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Pixels(bitmap);
    }

    private static IconImageMatcher.Raster Pixels(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return new(bitmap.PixelWidth, bitmap.PixelHeight, bytes);
    }
}
