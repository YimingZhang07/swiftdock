using System.IO;
using System.Resources;
using System.Windows.Media.Imaging;
using QuickLaunch.Interop;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class AppIconTests
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "QuickLaunch.ico");

    [Fact]
    public void IconHasExactlyEightDecodableFramesAndPngAt256()
    {
        Assert.True(File.Exists(IconPath));
        byte[] bytes = File.ReadAllBytes(IconPath);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        Assert.Equal(Sizes.Length, reader.ReadUInt16());
        int nextOffset = 6 + 16 * Sizes.Length;
        foreach (int size in Sizes)
        {
            Assert.Equal(size == 256 ? 0 : size, reader.ReadByte());
            Assert.Equal(size == 256 ? 0 : size, reader.ReadByte());
            Assert.Equal(0, reader.ReadUInt16());
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            int length = reader.ReadInt32(), offset = reader.ReadInt32();
            Assert.Equal(nextOffset, offset);
            Assert.InRange(length, 1, bytes.Length - offset);
            nextOffset += length;
            if (size == 256) Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[offset..(offset + 8)]);
        }
        Assert.Equal(bytes.Length, nextOffset);
        stream.Position = 0;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(Sizes, decoder.Frames.Select(f => f.PixelWidth).Order());
        foreach (var frame in decoder.Frames)
        {
            Assert.Equal(frame.PixelWidth, frame.PixelHeight);
            int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * frame.PixelHeight];
            frame.CopyPixels(pixels, stride, 0);
            Assert.Contains(pixels, b => b != 0);
        }
    }

    [Fact]
    public void WpfResourceMatchesTheCommittedIcon()
    {
        using var resources = typeof(App).Assembly.GetManifestResourceStream("QuickLaunch.g.resources");
        Assert.NotNull(resources);
        using var reader = new ResourceReader(resources);
        var entries = reader.GetEnumerator();
        Stream? icon = null;
        while (entries.MoveNext())
            if ((string)entries.Key == "assets/quicklaunch.ico") icon = Assert.IsAssignableFrom<Stream>(entries.Value);
        Assert.NotNull(icon);
        using var copy = new MemoryStream();
        icon.CopyTo(copy);
        Assert.Equal(File.ReadAllBytes(IconPath), copy.ToArray());
    }

    [Theory]
    [InlineData(96, 16)] [InlineData(120, 20)] [InlineData(144, 24)] [InlineData(192, 32)]
    [InlineData(240, 40)] [InlineData(288, 48)] [InlineData(384, 64)]
    public void TraySelectsNativeFrameForSmallIconMetric(uint dpi, int expected)
    {
        int pixels = NativeMethods.GetSystemMetricsForDpi(49, dpi);
        Assert.Equal(expected, pixels);
        using var icon = AppIcon.Load(pixels);
        Assert.Equal(expected, icon.Width);
        Assert.Equal(expected, icon.Height);
        using var bitmap = icon.ToBitmap();
        Assert.InRange(bitmap.GetPixel(0, 0).A, 0, 8);
        Assert.Equal(255, bitmap.GetPixel(expected / 2, expected / 2).A);
    }

    [Fact]
    public void TrayConstructionDisablesLeftClickDelay()
    {
        // Check the production initializer without requiring Explorer's notification area.
        // Construction policy is checked without creating a tray window.
        var assembly = typeof(AppIconTests).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".App.xaml.cs", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        Assert.Matches(@"_tray\s*=\s*new TaskbarIcon\s*\{[^}]*\bNoLeftClickDelay\s*=\s*true\b", reader.ReadToEnd());
    }
}
