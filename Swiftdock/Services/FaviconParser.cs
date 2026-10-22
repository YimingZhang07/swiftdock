using System.Net;

namespace Swiftdock.Services;

internal sealed record FaviconCandidate(Uri Url, string Type, int[] Sizes, bool Any);

// Deliberately small HTML tokenizer: no DOM, scripts, external resources or packages.
internal static class FaviconParser
{
    internal static bool IsWeb(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
    internal static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);
    internal static bool Allowed(Uri origin, Uri uri) => IsWeb(uri) && (!IsLocalHost(uri) || IsLocalHost(origin));

    private static bool IsLocalHost(Uri uri)
    {
        // Inspect the literal host only: no DNS lookups. Keep the item's original host as
        // the trust boundary even after redirects through public pages or icon CDNs.
        string host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (bytes.All(b => b == 0)) return true; // Unspecified, including scoped IPv6 literals.
        if (bytes.Length == 4)
            return bytes[0] is 10 or 127 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
        return (bytes[0] & 0xFE) == 0xFC || bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80
            || bytes[15] == 1 && bytes.Take(15).All(b => b == 0);
    }

    internal static IReadOnlyList<FaviconCandidate> Parse(string html, Uri finalUrl, Uri origin, int pixels)
    {
        var links = new List<Dictionary<string, string>>();
        Uri basis = finalUrl;
        bool hasBase = false;
        int position = 0;
        while (position < html.Length)
        {
            int start = html.IndexOf('<', position);
            if (start < 0) break;
            position = start + 1;
            if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = html.IndexOf("-->", position, StringComparison.Ordinal);
                position = end < 0 ? html.Length : end + 3; continue;
            }
            int nameStart = position;
            while (position < html.Length && char.IsAsciiLetterOrDigit(html[position])) position++;
            string tag = html[nameStart..position].ToLowerInvariant();
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (position < html.Length && html[position] != '>')
            {
                if (char.IsWhiteSpace(html[position]) || html[position] == '/') { position++; continue; }
                int keyStart = position;
                while (position < html.Length && !char.IsWhiteSpace(html[position]) && html[position] is not ('=' or '>' or '/')) position++;
                string key = html[keyStart..position];
                while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
                string value = "";
                if (position < html.Length && html[position] == '=')
                {
                    position++;
                    while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
                    char quote = position < html.Length && html[position] is '\'' or '"' ? html[position++] : '\0';
                    int valueStart = position;
                    while (position < html.Length && (quote != '\0' ? html[position] != quote : !char.IsWhiteSpace(html[position]) && html[position] != '>')) position++;
                    value = WebUtility.HtmlDecode(html[valueStart..position]);
                    if (quote != '\0' && position < html.Length) position++;
                }
                if (key.Length > 0) attributes.TryAdd(key, value);
                if (position == keyStart) position++;
            }
            if (position < html.Length) position++;
            if (tag is "script" or "style" or "textarea" or "title")
            {
                int end = html.IndexOf("</" + tag, position, StringComparison.OrdinalIgnoreCase);
                position = end < 0 ? html.Length : end; continue;
            }
            if (tag == "base" && !hasBase && attributes.TryGetValue("href", out var baseHref)
                && Uri.TryCreate(finalUrl, baseHref, out var baseUrl)) { basis = baseUrl; hasBase = true; }
            if (tag == "link") links.Add(attributes);
        }
        var candidates = new List<FaviconCandidate>();
        foreach (var attributes in links)
        {
            var rel = attributes.GetValueOrDefault("rel", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!rel.Any(r => r.Equals("icon", StringComparison.OrdinalIgnoreCase) || r.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase)
                || r.Equals("apple-touch-icon-precomposed", StringComparison.OrdinalIgnoreCase))) continue;
            if (!attributes.TryGetValue("href", out var href) || string.IsNullOrWhiteSpace(href)
                || !Uri.TryCreate(basis, href, out var url) || !Allowed(origin, url)) continue;
            string type = attributes.GetValueOrDefault("type", "").Split(';')[0].Trim().ToLowerInvariant();
            string extension = System.IO.Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
            if (type.Contains("svg", StringComparison.Ordinal) || extension == ".svg") continue;
            var sizes = attributes.GetValueOrDefault("sizes", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var dimensions = new List<int>();
            foreach (string size in sizes)
            {
                string[] parts = size.Split('x', 'X');
                if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height)
                    && width > 0 && height > 0) dimensions.Add(Math.Max(width, height));
            }
            candidates.Add(new(url, type, dimensions.ToArray(), sizes.Contains("any", StringComparer.OrdinalIgnoreCase)));
        }
        int Size(FaviconCandidate c) => c.Sizes.Where(s => s >= pixels).DefaultIfEmpty(c.Any ? pixels : 0).Min() is > 0 and var larger
            ? larger : c.Sizes.DefaultIfEmpty(0).Max();
        bool Raster(FaviconCandidate c) => c.Type is "image/png" or "image/x-icon" or "image/vnd.microsoft.icon" or "image/jpeg" or "image/gif" or "image/bmp" or "image/webp"
            || System.IO.Path.GetExtension(c.Url.AbsolutePath).ToLowerInvariant() is ".png" or ".ico" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        var fallbacks = new[] { new Uri(Origin(finalUrl) + "/favicon.ico"), new Uri(Origin(origin) + "/favicon.ico") }
            .Where(url => Allowed(origin, url)).Distinct().ToArray();
        return candidates.Where(c => !fallbacks.Contains(c.Url)).OrderByDescending(Raster).ThenBy(c => Size(c) >= pixels ? 0 : 1)
            .ThenBy(c => Size(c) >= pixels ? Size(c) : -Size(c)).DistinctBy(c => c.Url)
            .Concat(fallbacks.Select(url => new FaviconCandidate(url, "image/x-icon", [], false))).ToArray();
    }
}
