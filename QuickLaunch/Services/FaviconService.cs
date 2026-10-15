using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using QuickLaunch.Diagnostics;

namespace QuickLaunch.Services;

internal sealed class FaviconService : IDisposable
{
    internal const int HtmlLimit = 512 * 1024, ImageLimit = 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateClient(new SocketsHttpHandler
    {
        UseCookies = false, AllowAutoRedirect = false, MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All
    });
    private static readonly SemaphoreSlim Sites = new(4);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _directory;
    private readonly ILogger<FaviconService> _log;
    private readonly Func<BitmapSource, int, Task<BitmapSource>> _render;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _requestTimeout, _siteTimeout;
    private readonly ConcurrentDictionary<string, Site> _sites = new();
    private readonly ConcurrentDictionary<string, Entry> _memory = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _pending = new();
    private readonly object _lifetime = new();
    private CancellationTokenSource _enabledStop = new();
    private bool _enabled = true, _disposed;
    private int _epoch;
    private sealed class Site
    {
        internal readonly object Sync = new();
        internal readonly SemaphoreSlim Gate = new(1);
        internal readonly SemaphoreSlim CacheGate = new(1);
        internal int Revision;
        internal Task<Result>? Pending;
        internal CancellationTokenSource Stop = new();
    }
    private sealed record Entry(BitmapSource? Image, DateTimeOffset Expires);
    private sealed record Result(BitmapSource? Source, string Outcome);

    internal FaviconService(string directory, ILogger<FaviconService> log, Func<BitmapSource, int, Task<BitmapSource>> render,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null, TimeSpan? requestTimeout = null, TimeSpan? siteTimeout = null)
    {
        _directory = directory; _log = log; _render = render; _now = now ?? (() => DateTimeOffset.UtcNow);
        _client = handler is null ? SharedClient : CreateClient(handler); _ownsClient = handler is not null;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(4); _siteTimeout = siteTimeout ?? TimeSpan.FromSeconds(10);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"QuickLaunch/{typeof(FaviconService).Assembly.GetName().Version?.ToString(3) ?? "1.0"} (favicon)");
        return client;
    }

    internal bool Enabled
    {
        get { lock (_lifetime) return _enabled && !_disposed; }
        set
        {
            lock (_lifetime)
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;
                _enabledStop.Cancel(); _enabledStop = new(); _epoch++;
                foreach (var site in _sites.Values)
                    lock (site.Sync) site.Pending = null;
            }
        }
    }

    internal static bool TryUrl(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !FaviconParser.IsWeb(parsed)) return false;
        uri = new UriBuilder(parsed) { Fragment = "" }.Uri; return true;
    }

    internal Task<BitmapSource?> GetAsync(Uri url, int pixels, CancellationToken cancellation)
    {
        CancellationToken enabledToken;
        int epoch;
        lock (_lifetime)
        {
            if (!_enabled || _disposed) return Task.FromResult<BitmapSource?>(null);
            enabledToken = _enabledStop.Token;
            epoch = _epoch;
        }
        string origin = FaviconParser.Origin(url);
        var site = _sites.GetOrAdd(origin, _ => new());
        int revision;
        lock (site.Sync) revision = site.Revision;
        string key = CacheKey(origin, pixels), pendingKey = $"{key}-{revision}";
        if (_memory.TryGetValue(pendingKey, out var cached) && cached.Expires > _now()) return Task.FromResult(cached.Image);
        string workKey = $"{pendingKey}-{epoch}";
        var lazy = _pending.GetOrAdd(workKey, _ => new(() => Task.Run(() => LoadAsync(url, pixels, key, pendingKey, site, revision, enabledToken))));
        var task = lazy.Value;
        _ = task.ContinueWith(_ => _pending.TryRemove(new KeyValuePair<string, Lazy<Task<BitmapSource?>>>(workKey, lazy)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(cancellation);
    }

    internal static string CacheKey(string origin, int pixels) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{origin}|{pixels}|favicon-v1")));

    internal DateTimeOffset Expires(Uri url, int pixels)
    {
        string origin = FaviconParser.Origin(url);
        if (!_sites.TryGetValue(origin, out var site)) return DateTimeOffset.MinValue;
        int revision;
        lock (site.Sync) revision = site.Revision;
        return _memory.TryGetValue($"{CacheKey(origin, pixels)}-{revision}", out var entry) ? entry.Expires : DateTimeOffset.MinValue;
    }

    internal void Refresh(Uri url)
    {
        var site = _sites.GetOrAdd(FaviconParser.Origin(url), _ => new());
        lock (site.Sync)
        {
            site.Revision++; site.Stop.Cancel(); site.Stop = new(); site.Pending = null;
        }
    }

    private async Task<BitmapSource?> LoadAsync(Uri url, int pixels, string key, string pendingKey, Site site, int revision, CancellationToken enabledToken)
    {
        Task<Result>? discovery = null;
        try
        {
            enabledToken.ThrowIfCancellationRequested();
            string path = Path.Combine(_directory, key + ".favicon");
            var entry = revision == 0 ? ReadCache(path) : null;
            if (entry is null)
            {
                lock (site.Sync)
                {
                    if (revision != site.Revision) return null;
                    var refreshToken = site.Stop.Token;
                    discovery = site.Pending ??= Task.Run(() => DiscoverAsync(url, pixels, site, refreshToken, enabledToken));
                }
                var result = await discovery.ConfigureAwait(false);
                enabledToken.ThrowIfCancellationRequested();
                var image = result.Source is null ? null : await _render(result.Source, pixels).ConfigureAwait(false);
                entry = new(image, _now().AddDays(image is null ? 1 : 30));
                await site.CacheGate.WaitAsync(enabledToken).ConfigureAwait(false);
                try
                {
                    lock (site.Sync) { if (revision != site.Revision) return null; }
                    // Serialize writes without holding the lock used by UI refresh/settings.
                    WriteCache(path, entry);
                }
                finally { site.CacheGate.Release(); }
            }
            lock (site.Sync)
            {
                if (revision != site.Revision || enabledToken.IsCancellationRequested) return null;
                if (_memory.Count >= 512) _memory.Clear();
                _memory[pendingKey] = entry;
            }
            return entry.Image;
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            lock (site.Sync)
                if (discovery is not null && ReferenceEquals(site.Pending, discovery) && discovery.IsCompleted) site.Pending = null;
        }
    }

    private async Task<Result> DiscoverAsync(Uri url, int pixels, Site site, CancellationToken refreshToken, CancellationToken enabledToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(refreshToken, enabledToken);
        bool siteHeld = false, slotHeld = false;
        var watch = new Stopwatch();
        string outcome = "cancelled";
        try
        {
            await site.Gate.WaitAsync(stop.Token).ConfigureAwait(false); siteHeld = true;
            await Sites.WaitAsync(stop.Token).ConfigureAwait(false); slotHeld = true;
            watch.Start(); stop.CancelAfter(_siteTimeout);
            var page = await FetchAsync(url, url, html: true, stop.Token).ConfigureAwait(false);
            var candidates = FaviconParser.Parse(page is null ? "" : DecodeHtml(page.Bytes, page.Charset), page?.Url ?? url, url, pixels);
            outcome = "unavailable";
            foreach (var candidate in candidates)
            {
                try
                {
                    var content = await FetchAsync(candidate.Url, url, html: false, stop.Token).ConfigureAwait(false);
                    if (content is null) continue;
                    using var stream = new MemoryStream(content.Bytes, writable: false);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var frame = IconService.ClosestFrame(decoder, pixels);
                    // Bound decoded allocations as well as compressed input (favicons need no huge canvases).
                    if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) continue;
                    frame.Freeze(); outcome = "success"; return new(frame, outcome);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or NotSupportedException or ArgumentException
                    or FormatException or InvalidOperationException or COMException) { outcome = "undecodable"; }
            }
            return new(null, outcome);
        }
        catch (OperationCanceledException)
        {
            outcome = refreshToken.IsCancellationRequested || enabledToken.IsCancellationRequested ? "cancelled" : "timeout";
            if (outcome == "cancelled") throw;
            return new(null, outcome);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or InvalidOperationException)
        { outcome = "failure"; return new(null, outcome); }
        finally
        {
            if (watch.IsRunning) _log.FaviconResult(FaviconParser.Origin(url), outcome, watch.ElapsedMilliseconds);
            if (slotHeld) Sites.Release();
            if (siteHeld) site.Gate.Release();
        }
    }

    private sealed record Content(Uri Url, byte[] Bytes, string? Charset);
    private async Task<Content?> FetchAsync(Uri url, Uri origin, bool html, CancellationToken cancellation)
    {
        for (int redirects = 0; ; redirects++)
        {
            if (!FaviconParser.Allowed(origin, url)) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(_requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 5 || response.Headers.Location is not { } location || !Uri.TryCreate(url, location, out var next)
                    || !FaviconParser.Allowed(origin, next)) return null;
                url = next; continue;
            }
            // Retain the final page URL for its favicon.ico fallback even without usable HTML.
            if (!response.IsSuccessStatusCode) return html ? new(url, [], null) : null;
            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (html ? type != "text/html" : !type.StartsWith("image/", StringComparison.Ordinal) || type.Contains("svg", StringComparison.Ordinal))
                return html ? new(url, [], null) : null;
            int limit = html ? HtmlLimit : ImageLimit;
            if (!html && response.Content.Headers.ContentLength > limit) return null;
            using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var target = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (target.Length < limit)
            {
                int count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)target.Length)), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                target.Write(buffer, 0, count);
            }
            if (!html && target.Length == limit && await source.ReadAsync(buffer.AsMemory(0, 1), timeout.Token).ConfigureAwait(false) != 0) return null;
            return new(url, target.ToArray(), response.Content.Headers.ContentType?.CharSet);
        }
    }

    private static string DecodeHtml(byte[] bytes, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        try { if (!string.IsNullOrWhiteSpace(charset)) encoding = Encoding.GetEncoding(charset.Trim('"', '\'')); }
        catch (ArgumentException) { }
        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private Entry? ReadCache(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is < 8 or > ImageLimit) return null;
            using var reader = new BinaryReader(file);
            var expires = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            if (expires <= _now()) return null;
            if (file.Position == file.Length) return new(null, expires);
            var decoder = BitmapDecoder.Create(new MemoryStream(reader.ReadBytes((int)(file.Length - file.Position))),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var image = decoder.Frames[0]; image.Freeze(); return new(image, expires);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or FormatException or InvalidOperationException or COMException) { return null; }
    }

    private void WriteCache(string path, Entry entry)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            using (var file = File.Create(temporary))
            {
                using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
                writer.Write(entry.Expires.UtcTicks);
                if (entry.Image is not null)
                {
                    // WIC seeks to the start of its output; encode separately from the expiry header.
                    using var png = new MemoryStream();
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(entry.Image)); encoder.Save(png);
                    png.Position = 0; png.CopyTo(file);
                }
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Disposable cache. */ }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public void Dispose()
    {
        lock (_lifetime) { _disposed = true; _enabledStop.Cancel(); }
        if (_ownsClient) _client.Dispose();
        _memory.Clear();
    }
}
