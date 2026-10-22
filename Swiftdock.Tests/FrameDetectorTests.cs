using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swiftdock.E2E;
using Xunit;
using Xunit.Abstractions;

namespace Swiftdock.Tests;

public sealed class FrameDetectorTests(ITestOutputHelper output)
{
    [Fact]
    public void ReviewerScreenshotsDistinguishClassicFrameFromNormalBorder()
    {
        static FrameDetector.Sample Read(string file)
        {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "Frames", file));
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return FrameDetector.Analyze(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        var normal = Read("fixed-frame.png");
        var old = Read("old-frame.png");
        output.WriteLine("Fixed: " + normal); output.WriteLine("Old: " + old);
        Assert.False(normal.HasFrame);
        Assert.True(old.HasFrame, "The real old-code frame must be detected, including its incomplete top edge.");
        Assert.True(old.IncreaseFrom(normal) > .9);
        Assert.Equal(0, normal.IncreaseFrom(normal));
    }
}
