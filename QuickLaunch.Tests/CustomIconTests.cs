using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using QuickLaunch.Models;
using QuickLaunch.Services;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class FluentFontFactAttribute : FactAttribute
{
    public FluentFontFactAttribute()
    {
        if (IconCatalog.FindTypeface("Segoe Fluent Icons") is null)
            Skip = "Segoe Fluent Icons is not installed; Windows 10 uses the available Segoe MDL2 Assets subset.";
    }
}

public sealed class SystemIconsFactAttribute : FactAttribute
{
    public SystemIconsFactAttribute()
    {
        if (!File.Exists(Environment.ExpandEnvironmentVariables(SystemIconCatalog.Libraries[0])))
            Skip = "Windows imageres.dll is not installed.";
    }
}

public sealed class CustomIconTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuickLaunch-custom-icons-" + Guid.NewGuid().ToString("N"));
    private static LaunchItem Web(string? icon = null) => new("Web", "https://example.com", LaunchKind.Url) { Icon = icon };

    [FluentFontFact]
    public void EveryLibraryGlyphExistsInFluentIcons()
    {
        var font = IconCatalog.FindTypeface("Segoe Fluent Icons")!;
        Assert.InRange(IconCatalog.Glyphs.Count, 56, 64);
        Assert.Equal(IconCatalog.Glyphs.Count, IconCatalog.Glyphs.Select(g => g.CodePoint).Distinct().Count());
        Assert.Empty(IconCatalog.Glyphs.Where(g => !font.CharacterToGlyphMap.TryGetValue(g.CodePoint, out ushort index) || index == 0)
            .Select(g => $"Missing {g.Name}: U+{g.CodePoint:X4}"));
    }

    [Fact]
    public void RuntimeLibraryOnlyShowsSupportedGlyphsAndUsesThePreferredFont()
    {
        Assert.Equal(IconCatalog.FindTypeface("Segoe Fluent Icons") is null ? "Segoe MDL2 Assets" : "Segoe Fluent Icons", IconCatalog.FontName);
        Assert.All(IconCatalog.AvailableGlyphs, g => Assert.True(IconCatalog.Typeface!.CharacterToGlyphMap.ContainsKey(g.CodePoint)));
        Assert.Equal(8, IconCatalog.Colors.Count);
        Assert.All(IconCatalog.Glyphs, g => { Assert.False(string.IsNullOrWhiteSpace(g.Name)); Assert.False(string.IsNullOrWhiteSpace(g.Group)); });
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("glyph:E943")] [InlineData("glyph:ZZZZ:#2F6FDB")]
    [InlineData("glyph:FFFF:#2F6FDB")] [InlineData("glyph:E943:#FFFFFF")] [InlineData("glyph:E943:#2F6FDB:extra")]
    [InlineData("file:")] [InlineData("file:relative.ico")] [InlineData("file:C:\\test.svg")]
    [InlineData("file:C:\\test.png,1")] [InlineData("file:C:\\test.exe,x")] [InlineData("file:C:\\test.dll,2147483648")]
    [InlineData("file:C:\\bad?.ico")] [InlineData("unknown:E943")]
    [InlineData("fluent:unknown")] [InlineData("fluent:../apps")] [InlineData("fluent:Apps")]
    public void MalformedIconsNormalizeToNullWithoutRejectingConfig(string? value)
    {
        Assert.Null(IconSpec.Parse(value));
        var config = new AppConfig { Groups = [new() { Items = [Web(value)] }] };
        Assert.Null(ConfigValidation.Normalize(config).Groups[0].Items[0].Icon);
        Assert.Null(ConfigValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.AppConfig)).Groups[0].Items[0].Icon);
    }

    [Theory]
    [InlineData("false")] [InlineData("123")] [InlineData("[]")] [InlineData("{\"bad\":true}")]
    public void NonStringIconValuesAreAlsoOptional(string value)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new AppConfig { Groups = [new() { Items = [Web()] }] }, ConfigJsonContext.Default.AppConfig))!;
        json["groups"]![0]!["items"]![0]!["icon"] = System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.Null(ConfigValidation.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())).Groups[0].Items[0].Icon);
    }

    [Theory]
    [InlineData("glyph:e943:#2f6fdb", "glyph:E943:#2F6FDB")]
    [InlineData("file:C:\\icons\\app.dll,-42", "file:C:\\icons\\app.dll,-42")]
    [InlineData("file:C:\\icons\\app.exe,0", "file:C:\\icons\\app.exe,0")]
    [InlineData("file:C:\\icons\\a,b.ico", "file:C:\\icons\\a,b.ico")]
    [InlineData("fluent:apps", "fluent:apps")]
    [InlineData("file:%SystemRoot%\\System32\\imageres.dll,-3", "file:%SystemRoot%\\System32\\imageres.dll,-3")]
    public void ParseAndFormatAreCanonicalAndRoundTrip(string value, string expected)
    {
        Assert.Equal(expected, IconSpec.Parse(value)!.ToString());
        Assert.Equal(IconSpec.Parse(value), IconSpec.Parse(expected));
    }

    [Fact]
    public void FilePathsArePortableAndIconNeverChangesIdentity()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "pictures", "custom.ico");
        Assert.Equal("file:%USERPROFILE%\\pictures\\custom.ico", IconSpec.Normalize("file:" + path));
        Assert.Equal(ItemIdentity.Key(Web()), ItemIdentity.Key(Web("glyph:E943:#2F6FDB")));
        Assert.NotEqual(IconService.SourceKey(Web()), IconService.SourceKey(Web("glyph:E943:#2F6FDB")));
        Assert.NotEqual(IconService.SourceKey(Web("glyph:E943:#2F6FDB")), IconService.SourceKey(Web("glyph:E943:#0F7B6C")));
    }

    [Fact]
    public void LegacyJsonMigratesAndCustomIconRoundTrips()
    {
        // Literal baseline from the pre-icon schema, including its property ordering/indentation.
        const string legacy = """
            {
              "version": 1,
              "hotkey": {
                "modifiers": [
                  "Alt"
                ],
                "key": "Space"
              },
              "iconSize": 32,
              "columns": 6,
              "lastGroupId": "11111111111111111111111111111111",
              "groups": [
                {
                  "id": "11111111111111111111111111111111",
                  "name": "group",
                  "items": [
                    {
                      "name": "web",
                      "path": "https://example.com/",
                      "kind": "Url",
                      "arguments": "",
                      "workingDirectory": "",
                      "runAsAdmin": false,
                      "id": "22222222222222222222222222222222",
                      "launchCount": 0
                    }
                  ]
                }
              ]
            }
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(legacy.ReplaceLineEndings("\r\n"));
        var config = ConfigValidation.Parse(bytes);
        Assert.Equal(80, config.Scale); Assert.Null(config.IconSize);
        foreach (string icon in new[] { "glyph:E943:#2F6FDB", "file:C:\\icons\\app.dll,-42", "fluent:apps" })
        {
            config.Groups[0].Items[0] = config.Groups[0].Items[0] with { Icon = icon };
            byte[] custom = JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.AppConfig);
            Assert.Contains("\"icon\":", Encoding.UTF8.GetString(custom));
            Assert.Equal(icon, ConfigValidation.Parse(custom).Groups[0].Items[0].Icon);
        }
        config.Groups[0].Items[0] = config.Groups[0].Items[0] with { Icon = null };
        Assert.Equal(80, config.Scale); Assert.Null(config.IconSize);
    }

    [Theory]
    [InlineData(32)] [InlineData(40)] [InlineData(48)] [InlineData(64)] [InlineData(80)] [InlineData(96)]
    public async Task GlyphRendersAtPhysicalSizeWithWhiteCentreAndTransparentCorners(int pixels)
    {
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        // Filled star has a solid centre, unlike outline icons whose centre is intentionally empty.
        var image = await icons.GetAsync(Web("glyph:E735:#2F6FDB"), pixels);
        Assert.NotNull(image); Assert.True(image.IsFrozen);
        Assert.Equal(pixels, image.PixelWidth); Assert.Equal(pixels, image.PixelHeight);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, pixels / 2, pixels / 2));
        Assert.Equal(0, Pixel(image, 0, 0)[3]);
        Assert.Equal(0, Pixel(image, pixels - 1, pixels - 1)[3]);
        byte[] top = Pixel(image, pixels / 2, 2), bottom = Pixel(image, pixels / 2, pixels - 3);
        Assert.Equal(255, top[3]); Assert.True(top[0] > bottom[0] && top[1] > bottom[1] && top[2] > bottom[2]);
    }

    [Fact]
    public async Task GlyphCacheReusesMemoryAndDiskAndSeparatesSpecifications()
    {
        await using var log = new TestLogging();
        var item = Web("glyph:E943:#2F6FDB");
        using (var icons = log.CreateIcons(_directory))
        {
            var image = await icons.GetAsync(item, 48);
            Assert.NotNull(image); Assert.Same(image, await icons.GetAsync(item, 48));
            Assert.NotSame(image, await icons.GetAsync(item with { Icon = "glyph:E943:#0F7B6C" }, 48));
            Assert.Equal(2, icons.Extractions);
        }
        using (var icons = log.CreateIcons(_directory))
        {
            Assert.NotNull(await icons.GetAsync(item, 48)); Assert.Equal(1, icons.DiskHits); Assert.Equal(0, icons.Extractions);
        }
    }

    [Theory]
    [InlineData(16, 255, 0, 0)] [InlineData(32, 0, 255, 0)] [InlineData(40, 0, 255, 0)]
    [InlineData(48, 0, 255, 0)] [InlineData(60, 0, 255, 0)] [InlineData(80, 0, 0, 255)] [InlineData(96, 0, 0, 255)]
    public async Task IcoDecoderChoosesClosestFrameAndPrefersLargerTie(int pixels, byte red, byte green, byte blue)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "frames.ico"); WriteIco(path);
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        var image = await icons.GetAsync(Web("file:" + path), pixels);
        Assert.NotNull(image); Assert.Equal(pixels, image.PixelWidth); Assert.Equal(pixels, image.PixelHeight);
        Assert.Equal(new byte[] { blue, green, red, 255 }, Pixel(image, pixels / 2, pixels / 2));
    }

    [Theory]
    [InlineData(".png")] [InlineData(".jpg")] [InlineData(".bmp")]
    public async Task BitmapFilesAreDecodedAndFitWithoutDistortion(string extension)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "wide" + extension);
        BitmapEncoder encoder = extension switch { ".jpg" => new JpegBitmapEncoder(), ".bmp" => new BmpBitmapEncoder(), _ => new PngBitmapEncoder() };
        encoder.Frames.Add(BitmapFrame.Create(Solid(48, 24, Colors.Red)));
        using (var file = File.Create(path)) encoder.Save(file);
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        var image = await icons.GetAsync(Web("file:" + path), 48);
        Assert.NotNull(image); Assert.Equal(48, image.PixelWidth); Assert.Equal(48, image.PixelHeight);
        Assert.InRange(Pixel(image, 24, 24)[2], 250, 255); Assert.Equal(0, Pixel(image, 24, 0)[3]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrCorruptCustomFileFallsBackToDefaultWithoutError(bool corrupt)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "unavailable.ico");
        if (corrupt) await File.WriteAllTextAsync(path, "invalid image");
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        var item = Web("file:" + path);
        var expected = await icons.GetAsync(Web(), 48); var actual = await icons.GetAsync(item, 48);
        Assert.NotNull(expected); Assert.NotNull(actual);
        Assert.Equal(Pixels(expected), Pixels(actual)); Assert.Null(item.ErrorMessage);
    }

    [Fact]
    public async Task CustomFileCanPreviewBeforeTargetEntry()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "preview.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Solid(48, 48, Colors.Red)));
        using (var file = File.Create(path)) encoder.Save(file);
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        foreach (LaunchKind kind in Enum.GetValues<LaunchKind>())
        {
            var image = await icons.GetAsync(new LaunchItem("", "", kind) { Icon = "file:" + path }, 48);
            Assert.NotNull(image); Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(image, 24, 24));
        }
    }

    [Theory]
    [InlineData(".exe")] [InlineData(".dll")]
    public async Task ResourceIndexUsesTheFixturePeIconAndInvalidIndexFallsBack(string extension)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "resources" + extension);
        File.Copy(typeof(IconService).Assembly.Location, path);
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        var expected = await icons.GetAsync(Web(), 48);
        var custom = await icons.GetAsync(Web("file:" + path + ",0"), 48);
        Assert.NotNull(expected); Assert.NotNull(custom);
        Assert.Equal(48, custom.PixelWidth); Assert.NotEqual(Pixels(expected), Pixels(custom));
        var missing = await icons.GetAsync(Web("file:" + path + ",2147483647"), 48);
        Assert.NotNull(missing); Assert.Equal(Pixels(expected), Pixels(missing));
    }

    [Fact]
    public async Task EveryFluentColorRendersAndSharesMemoryAndDiskAcrossTargets()
    {
        await using var log = new TestLogging();
        using (var icons = log.CreateIcons(_directory))
        {
            Assert.Equal(96, FluentColorCatalog.Icons.Count);
            foreach (var entry in FluentColorCatalog.Icons)
            foreach (int pixels in new[] { 32, 64 })
            {
                // Empty file target has no fallback: a failed SVG cannot pass via a globe.
                var item = new LaunchItem("", "", LaunchKind.File) { Icon = entry.Spec };
                var image = await icons.GetAsync(item, pixels);
                Assert.True(image is not null, entry.Spec);
                Assert.True(image.IsFrozen); Assert.Equal(pixels, image.PixelWidth); Assert.Equal(pixels, image.PixelHeight);
                Assert.Contains(Pixels(image).Where((_, i) => i % 4 == 3), alpha => alpha > 0);
                Assert.Same(image, await icons.GetAsync(Web(entry.Spec), pixels));
            }
            Assert.Equal(192, icons.Extractions);
        }
        using (var icons = log.CreateIcons(_directory))
        {
            Assert.NotNull(await icons.GetAsync(Web("fluent:apps"), 64));
            Assert.Equal(1, icons.DiskHits); Assert.Equal(0, icons.Extractions);
        }
    }

    [SystemIconsFact]
    public async Task SystemResourceIdsEnumerateAndExtractWithoutOrdinalFallback()
    {
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        var entries = await icons.GetSystemIconsAsync();
        output.WriteLine(string.Join(", ", entries.GroupBy(e => IconSpec.Parse(e.Spec)!.Path)
            .Select(g => $"{g.Key}: {g.Count()} icons")));
        var first = Assert.IsType<CatalogIcon>(entries.FirstOrDefault());
        Assert.StartsWith("file:%SystemRoot%\\System32\\imageres.dll,-", first.Spec);
        Assert.All(entries, e => Assert.True(IconSpec.Parse(e.Spec)!.Index < 0));
        Assert.Equal(entries.Count, entries.Select(e => e.Spec).Distinct().Count());
        var image = await icons.GetAsync(new LaunchItem("", "", LaunchKind.File) { Icon = first.Spec }, 48);
        Assert.NotNull(image);
        Assert.Contains(Pixels(image).Where((_, i) => i % 4 == 3), alpha => alpha > 0);
    }

    [Fact]
    public async Task SystemPickerRejectsInvisibleResourcesAndCachesSuccessAndFailurePerSize()
    {
        Directory.CreateDirectory(_directory);
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(_directory);
        // Windows has fully transparent resources and faint empty frames; black ink
        // and a thin opaque outline must still be accepted in either theme.
        foreach (int pixels in new[] { 28, 56 })
        foreach (string kind in new[] { "transparent", "faint-frame", "speck", "black", "outline", "missing" })
        {
            string path = Path.Combine(_directory, $"{kind}-{pixels}.png");
            if (kind != "missing")
            {
                byte[] bytes = new byte[pixels * pixels * 4];
                for (int y = 0; y < pixels; y++)
                for (int x = 0; x < pixels; x++)
                {
                    int index = (y * pixels + x) * 4;
                    bool edge = x == 0 || y == 0 || x == pixels - 1 || y == pixels - 1;
                    bytes[index + 3] = kind switch
                    {
                        "faint-frame" when edge => 77, "outline" when edge => 255,
                        "black" => 255, "speck" when x == pixels / 2 && y == pixels / 2 => 255, _ => 0
                    };
                }
                var bitmap = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Bgra32, null, bytes, pixels * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(path); encoder.Save(stream);
            }
            var icon = new CatalogIcon(kind, "file:" + path);
            var image = await icons.GetSystemPickerImageAsync(icon, pixels);
            bool visible = kind is "black" or "outline";
            Assert.Equal(visible, image is not null);
            Assert.Equal(!visible, icons.IsEmptySystemIcon(icon.Spec, pixels));
            long extractions = icons.Extractions;
            Assert.Same(image, await icons.GetSystemPickerImageAsync(icon, pixels));
            Assert.Equal(extractions, icons.Extractions);
        }
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes;
    }
    private static byte[] Pixel(BitmapSource image, int x, int y) => Pixels(image).Skip((y * image.PixelWidth + x) * 4).Take(4).ToArray();
    private static BitmapSource Solid(int width, int height, Color color)
    {
        byte[] bytes = Enumerable.Range(0, width * height).SelectMany(_ => new[] { color.B, color.G, color.R, color.A }).ToArray();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4); image.Freeze(); return image;
    }
    private static void WriteIco(string path)
    {
        int[] sizes = [16, 48, 96]; Color[] colors = [Colors.Red, Colors.Lime, Colors.Blue];
        var frames = sizes.Select((size, i) =>
        {
            using var buffer = new MemoryStream(); var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(Solid(size, size, colors[i]))); png.Save(buffer); return buffer.ToArray();
        }).ToArray();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Length);
        int offset = 6 + frames.Length * 16;
        for (int i = 0; i < frames.Length; i++)
        {
            writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (string file in Directory.GetFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
