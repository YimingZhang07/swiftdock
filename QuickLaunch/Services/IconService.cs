using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows;
using Microsoft.Extensions.Logging;
using QuickLaunch.Diagnostics;
using QuickLaunch.Interop;
using QuickLaunch.Models;

namespace QuickLaunch.Services;

public sealed class IconService : IDisposable
{
    private readonly string _directory;
    private readonly ILogger<IconService> _log;
    private readonly FaviconService _favicons;
    private static readonly ConcurrentDictionary<int, BitmapSource> Globes = new();
    public bool AutoFetchWebsiteIcons { get => _favicons.Enabled; set => _favicons.Enabled = value; }
    private readonly ConcurrentDictionary<string, BitmapSource> _memory = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _pending = new();
    private readonly ConcurrentDictionary<string, int> _revisions = new();
    private readonly SemaphoreSlim _ioSlots = new(4);
    private readonly CancellationTokenSource _stop = new();
    private readonly IconThread _thread;
    private long _memoryHits, _diskHits, _extractions;
    private bool _disposed;
    public long MemoryHits => Interlocked.Read(ref _memoryHits);
    public long DiskHits => Interlocked.Read(ref _diskHits);
    public long Extractions => Interlocked.Read(ref _extractions);
    public int CachedCount => _memory.Count;
    public string DirectoryPath => _directory;

    internal IconService(string directory, ILogger<IconService> log, FaviconService favicons, IconThread thread)
    {
        _directory = directory; _log = log; _favicons = favicons; _thread = thread;
    }

    public static int PixelSize(double dip, double scale) => Math.Clamp((int)Math.Ceiling(dip * scale), 16, 256);
    public static string SourceKey(LaunchItem item)
    {
        string source = item.Kind == LaunchKind.Url ? "v1:globe" : $"v1:{item.Kind}:{Environment.ExpandEnvironmentVariables(item.Path).ToUpperInvariant()}";
        if (IconSpec.Parse(item.Icon) is { } custom) source += $"|custom-v1:{custom}|{(custom.IsGlyph ? IconCatalog.FontName : "file")}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    public async Task<BitmapSource?> GetAsync(LaunchItem item, int pixels, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        pixels = Math.Clamp(pixels, 16, 256);
        string source = SourceKey(item);
        int revision = _revisions.GetValueOrDefault(source);
        string key = $"{source}-{pixels}-{revision}";
        if (_memory.TryGetValue(key, out var ready)) { Interlocked.Increment(ref _memoryHits); return ready; }
        if (_pending.Count >= 512) return null;
        var lazy = _pending.GetOrAdd(key, _ => new(() => LoadAsync(item, pixels, source, revision, key)));
        var task = lazy.Value;
        _ = task.ContinueWith(_ => _pending.TryRemove(new KeyValuePair<string, Lazy<Task<BitmapSource?>>>(key, lazy)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(cancellation);
    }

    public Task<BitmapSource?> GetFaviconAsync(LaunchItem item, int pixels, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return UsesFavicon(item) && FaviconService.TryUrl(item.Path, out var url)
            ? _favicons.GetAsync(url, Math.Clamp(pixels, 16, 256), cancellation) : Task.FromResult<BitmapSource?>(null);
    }

    internal static bool UsesFavicon(LaunchItem item) => item.Kind == LaunchKind.Url && string.IsNullOrWhiteSpace(item.Icon);
    internal DateTimeOffset FaviconExpires(LaunchItem item, int pixels) => FaviconService.TryUrl(item.Path, out var url)
        ? _favicons.Expires(url, pixels) : DateTimeOffset.MaxValue;

    public void Refresh(LaunchItem item)
    {
        _revisions.AddOrUpdate(SourceKey(item), 1, (_, revision) => revision + 1);
        if (UsesFavicon(item) && FaviconService.TryUrl(item.Path, out var url)) _favicons.Refresh(url);
    }

    private async Task<BitmapSource?> LoadAsync(LaunchItem item, int pixels, string source, int revision, string key)
    {
        await _ioSlots.WaitAsync(_stop.Token);
        try
        {
            return await Task.Run(async () =>
            {
                string path = Path.Combine(_directory, $"{source}-{pixels}.png");
                string stamp = SourceStamp(item);
                BitmapSource? image = null;
                try
                {
                    if (revision == 0 && File.Exists(path) && File.ReadAllText(path + ".stamp") == stamp)
                    {
                        using var file = File.OpenRead(path);
                        if (file.Length > 1024 * 1024) throw new InvalidDataException("Invalid icon cache size.");
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = pixels; bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze();
                        image = bitmap;
                        Interlocked.Increment(ref _diskHits);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or COMException)
                { /* Cache is disposable; source extraction remains available. */ }
                if (image is null)
                {
                    var dispatcher = await _thread.Ready.WaitAsync(_stop.Token);
                    image = await dispatcher.InvokeAsync(() => Extract(item, pixels), DispatcherPriority.Background, _stop.Token).Task;
                    Interlocked.Increment(ref _extractions);
                    if (image is not null)
                    {
                        try
                        {
                            Directory.CreateDirectory(_directory);
                            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                using (var file = File.Create(temporary))
                                {
                                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file);
                                }
                                File.Move(temporary, path, overwrite: true);
                                File.WriteAllText(path + ".stamp", stamp);
                            }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { _log.IconCacheWriteFailed(ex.Message); }
                    }
                }
                if (image is not null && !_disposed && _revisions.GetValueOrDefault(source) == revision)
                {
                    if (_memory.Count >= 512) _memory.Clear(); // Visible visuals keep their frozen images.
                    _memory[key] = image;
                }
                return image;
            }, _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or OperationCanceledException or ArgumentException)
        { _log.IconUnavailable((int)item.Kind, ex.Message); return null; }
        finally { _ioSlots.Release(); }
    }

    private static string SourceStamp(LaunchItem item)
    {
        var custom = IconSpec.Parse(item.Icon);
        if (custom is { IsGlyph: true }) return "glyph-v1";
        static string FileStamp(string value) => File.Exists(value) && new FileInfo(value) is { Exists: true } file ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        string? customPath = custom?.Path is { } iconPath ? Environment.ExpandEnvironmentVariables(iconPath) : null;
        if (customPath is not null && File.Exists(customPath)) return "file-v1:" + FileStamp(customPath);
        string path = Environment.ExpandEnvironmentVariables(item.Path);
        string stamp = item.Kind == LaunchKind.Url ? "globe-v1" : item.Kind == LaunchKind.Folder
            ? Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks.ToString() : "missing" : FileStamp(path);
        if (customPath is not null) stamp += "|missing";
        return stamp;
    }

    private static BitmapSource? Extract(LaunchItem item, int pixels)
    {
        if (IconSpec.Parse(item.Icon) is { } custom)
        {
            try
            {
                var image = custom.IsGlyph ? RenderGlyph(custom, pixels) : ExtractFile(custom, pixels);
                if (image is not null) return image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                or FormatException or InvalidOperationException or COMException)
            { /* A custom icon is decorative: a bad/missing source must not mark the launch target broken. */ }
        }
        if (item.Kind == LaunchKind.Url) return Globe(pixels);
        return ExtractShell(Environment.ExpandEnvironmentVariables(item.Path), pixels);
    }

    private static BitmapSource? ExtractFile(IconSpec spec, int pixels)
    {
        string path = Environment.ExpandEnvironmentVariables(spec.Path!);
        if (!File.Exists(path)) return null;
        if (Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".dll"))
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            // Prefer the larger frame on an equal-distance tie; then the highest colour depth.
            var frame = ClosestFrame(decoder, pixels);
            return Fit(frame, pixels);
        }
        if (spec.Index is not { } index) return ExtractShell(path, pixels);
        nint icon = 0;
        try
        {
            uint count = NativeMethods.PrivateExtractIcons(path, index, pixels, pixels, out icon, out _, 1, 0);
            if (count is 0 or uint.MaxValue || icon == 0) return null;
            return Fit(Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()), pixels);
        }
        finally { if (icon != 0) NativeMethods.DestroyIcon(icon); }
    }

    private static BitmapSource Fit(BitmapSource source, int pixels)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        double scale = (double)pixels / Math.Max(source.PixelWidth, source.PixelHeight);
        double width = source.PixelWidth * scale, height = source.PixelHeight * scale;
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect((pixels - width) / 2, (pixels - height) / 2, width, height));
        return Render(visual, pixels);
    }

    internal static BitmapFrame ClosestFrame(BitmapDecoder decoder, int pixels) => decoder.Frames
        .OrderBy(f => Math.Abs(Math.Max(f.PixelWidth, f.PixelHeight) - pixels))
        .ThenByDescending(f => Math.Max(f.PixelWidth, f.PixelHeight)).ThenByDescending(f => f.Format.BitsPerPixel).First();

    internal static BitmapSource RenderFavicon(BitmapSource source, int pixels)
    {
        int size = Math.Max(source.PixelWidth, source.PixelHeight);
        if (size >= pixels * .75) return Fit(source, pixels);
        var visual = new DrawingVisual();
        double scale = Math.Max(1, Math.Floor(pixels * .62 / size));
        // A source between 62% and 75% needs a small high-quality downscale to fit the plate.
        if (size > pixels * .62) scale = pixels * .62 / size;
        RenderOptions.SetBitmapScalingMode(visual, scale == Math.Floor(scale) ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        double width = source.PixelWidth * scale, height = source.PixelHeight * scale;
        using (var drawing = visual.RenderOpen())
        {
            var border = new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)), 1);
            drawing.DrawRoundedRectangle(Brushes.White, border, new Rect(.5, .5, pixels - 1, pixels - 1), pixels * .22, pixels * .22);
            drawing.DrawImage(source, new Rect(Math.Round((pixels - width) / 2), Math.Round((pixels - height) / 2), width, height));
        }
        return Render(visual, pixels);
    }

    private static BitmapSource? RenderGlyph(IconSpec spec, int pixels)
    {
        if (IconCatalog.Typeface is not { } font || !font.CharacterToGlyphMap.TryGetValue(spec.CodePoint, out ushort glyph)) return null;
        var color = (Color)ColorConverter.ConvertFromString(spec.Color!);
        static Color Lightness(Color c, double amount) => Color.FromRgb(
            (byte)Math.Clamp(Math.Round(c.R + (amount > 0 ? (255 - c.R) * amount : c.R * amount)), 0, 255),
            (byte)Math.Clamp(Math.Round(c.G + (amount > 0 ? (255 - c.G) * amount : c.G * amount)), 0, 255),
            (byte)Math.Clamp(Math.Round(c.B + (amount > 0 ? (255 - c.B) * amount : c.B * amount)), 0, 255));
        var fill = new LinearGradientBrush(Lightness(color, .06), Lightness(color, -.06), new Point(0, 0), new Point(0, 1));
        var outline = font.GetGlyphOutline(glyph, pixels * .56, pixels * .56);
        Rect bounds = outline.Bounds;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushGuidelineSet(new GuidelineSet([0, pixels], [0, pixels]));
            drawing.DrawRoundedRectangle(fill, null, new Rect(0, 0, pixels, pixels), pixels * .22, pixels * .22);
            // Centre the ink, not the font's baseline/advance box, and align its origin to physical pixels.
            drawing.PushTransform(new TranslateTransform(Math.Round((pixels - bounds.Width) / 2 - bounds.X), Math.Round((pixels - bounds.Height) / 2 - bounds.Y)));
            drawing.DrawGeometry(Brushes.White, null, outline);
            drawing.Pop(); drawing.Pop();
        }
        return Render(visual, pixels);
    }

    private static BitmapSource Render(DrawingVisual visual, int pixels)
    {
        var image = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private static BitmapSource? ExtractShell(string path, int pixels)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        int result = NativeMethods.SHCreateItemFromParsingName(path, 0, in iid, out nint pointer);
        if (result < 0 || pointer == 0) return null;
        object? factory = null;
        nint bitmap = 0;
        try
        {
            factory = Marshal.GetObjectForIUnknown(pointer);
            result = ((IShellItemImageFactory)factory).GetImage(new NativeSize { Width = pixels, Height = pixels }, 0x5, out bitmap);
            if (result < 0 || bitmap == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
            Marshal.Release(pointer);
        }
    }

    internal static BitmapSource Globe(int pixels) => Globes.GetOrAdd(pixels, CreateGlobe);

    private static BitmapSource CreateGlobe(int pixels)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            double center = pixels / 2.0, radius = pixels * 0.4;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(42, 164, 150)), Math.Max(2, pixels / 18.0));
            drawing.DrawEllipse(null, pen, new Point(center, center), radius, radius);
            drawing.DrawEllipse(null, pen, new Point(center, center), radius * 0.45, radius);
            drawing.DrawLine(pen, new Point(center - radius, center), new Point(center + radius, center));
        }
        var image = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _memory.Clear();
    }
}
