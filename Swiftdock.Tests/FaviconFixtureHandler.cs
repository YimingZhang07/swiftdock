using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swiftdock.Tests;

// Shared by unit tests and desktop smoke. There is no network fallback.
internal sealed class FaviconFixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
{
    internal ConcurrentQueue<Uri> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        return respond is null ? Task.FromResult(Respond(request.RequestUri!)) : respond(request, cancellationToken);
    }
    internal static HttpResponseMessage Respond(Uri url)
    {
        if (url.Host is not ("large.test" or "tiny.test" or "medium.test")) return new(HttpStatusCode.NotFound);
        bool tiny = url.Host == "tiny.test";
        if (url.AbsolutePath == "/icon.png") return Image(Png(tiny ? 16 : 128), "image/png");
        if (url.AbsolutePath == "/favicon.ico") return Image(Ico(16), "image/x-icon");
        return Html(tiny ? "<html><head><link rel='shortcut icon' href='/favicon.ico' sizes='16x16'></head>" :
            "<html><head><link rel='apple-touch-icon' href='/icon.png' sizes='128x128'></head>");
    }
    internal static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    internal static HttpResponseMessage Image(byte[] bytes, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type); return response;
    }
    internal static BitmapSource Source(int size)
    {
        byte[] bytes = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int index = (y * size + x) * 4;
                bool mark = x >= size / 4 && x < size * 3 / 4 && y >= size / 4 && y < size * 3 / 4;
                // Tiny: a dark pixel-art diamond on transparency. Large: an opaque blue tile.
                bool visible = size > 16 || Math.Abs(x - 7) + Math.Abs(y - 7) <= 7;
                bytes[index] = size <= 16 ? (byte)36 : mark ? (byte)255 : (byte)208;
                bytes[index + 1] = size <= 16 ? (byte)30 : mark ? (byte)255 : (byte)112;
                bytes[index + 2] = size <= 16 ? (byte)28 : mark ? (byte)255 : (byte)48;
                bytes[index + 3] = visible ? (byte)255 : (byte)0;
            }
        var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bytes, size * 4);
        source.Freeze(); return source;
    }
    internal static byte[] Png(int size)
    {
        using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Source(size))); encoder.Save(stream); return stream.ToArray();
    }
    internal static byte[] Ico(int size)
    {
        byte[] png = Png(size);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)size); writer.Write((byte)size); writer.Write((ushort)0);
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(png.Length); writer.Write(22); writer.Write(png);
        return stream.ToArray();
    }
}
