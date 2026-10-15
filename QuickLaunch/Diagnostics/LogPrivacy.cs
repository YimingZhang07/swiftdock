using System.Text.RegularExpressions;
using QuickLaunch.Services;

namespace QuickLaunch.Diagnostics;

internal static partial class LogPrivacy
{
    // Shell/IO exception messages can echo a target URL. Keep only its origin.
    internal static string Message(string message) => !message.Contains("http", StringComparison.OrdinalIgnoreCase) ? message :
        WebUrl().Replace(message, match => Uri.TryCreate(match.Value, UriKind.Absolute, out var url) ? FaviconParser.Origin(url) : "[url]");

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WebUrl();
}
