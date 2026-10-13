using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickLaunch.Interop;
using QuickLaunch.Models;

namespace QuickLaunch.Services;

public sealed class IconService : IDisposable
{
    private readonly string _directory;
    private readonly DiagnosticLog _log;
    private readonly ConcurrentDictionary<string, BitmapSource> _memory = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _pending = new();
    private readonly ConcurrentDictionary<string, int> _revisions = new();
    private readonly SemaphoreSlim _ioSlots = new(4);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Dispatcher> _sta = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _memoryHits, _diskHits, _extractions;
    private bool _disposed;
    public long MemoryHits => Interlocked.Read(ref _memoryHits);
    public long DiskHits => Interlocked.Read(ref _diskHits);
    public long Extractions => Interlocked.Read(ref _extractions);
    public int CachedCount => _memory.Count;
    public string DirectoryPath => _directory;

    public IconService(string directory, DiagnosticLog log)
    {
        _directory = directory;
        _log = log;
        var thread = new Thread(() =>
        {
            int result = NativeMethods.OleInitialize(0);
            if (result < 0) { _sta.TrySetException(Marshal.GetExceptionForHR(result)!); return; }
            try { _sta.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }
            finally { NativeMethods.OleUninitialize(); }
        }) { IsBackground = true, Name = "QuickLaunch Shell icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static int PixelSize(double dip, double scale) => Math.Clamp((int)Math.Ceiling(dip * scale), 16, 256);
    public static string SourceKey(LaunchItem item) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        item.Kind == LaunchKind.Url ? "v1:globe" : $"v1:{item.Kind}:{Environment.ExpandEnvironmentVariables(item.Path).ToUpperInvariant()}")));

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

    public void Refresh(LaunchItem item) => _revisions.AddOrUpdate(SourceKey(item), 1, (_, revision) => revision + 1);

    private async Task<BitmapSource?> LoadAsync(LaunchItem item, int pixels, string source, int revision, string key)
    {
        await _ioSlots.WaitAsync(_stop.Token);
        try
        {
            return await Task.Run(async () =>
            {
                string path = Path.Combine(_directory, $"{source}-{pixels}.png");
                string stamp = item.Kind == LaunchKind.Url ? "globe-v1" : SourceStamp(item);
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
                    var dispatcher = await _sta.Task.WaitAsync(_stop.Token);
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
                        { _log.Write("icon.cache-write-failed", new { ex.Message }); }
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
        { _log.Write("icon.unavailable", new { item.Kind, ex.Message }); return null; }
        finally { _ioSlots.Release(); }
    }

    private static string SourceStamp(LaunchItem item)
    {
        string path = Environment.ExpandEnvironmentVariables(item.Path);
        return item.Kind == LaunchKind.Folder ? Directory.GetLastWriteTimeUtc(path).Ticks.ToString()
            : new FileInfo(path) is { Exists: true } file ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private static BitmapSource? Extract(LaunchItem item, int pixels)
    {
        if (item.Kind == LaunchKind.Url) return Globe(pixels);
        Guid iid = typeof(IShellItemImageFactory).GUID;
        int result = NativeMethods.SHCreateItemFromParsingName(Environment.ExpandEnvironmentVariables(item.Path), 0, in iid, out nint pointer);
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

    private static BitmapSource Globe(int pixels)
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
        if (_sta.Task.IsCompletedSuccessfully) _sta.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
        _memory.Clear();
        // A blocked third-party Shell extension cannot be force-cancelled; its thread is background.
    }
}
