using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickLaunch.Services;

public sealed record IconSpec
{
    public int CodePoint { get; private init; }
    public string? Color { get; private init; }
    public string? Path { get; private init; }
    public int? Index { get; private init; }
    public bool IsGlyph => Path is null;

    public static IconSpec? Parse(string? value)
    {
        if (value is null || value.Length > 32767) return null;
        if (value.StartsWith("glyph:", StringComparison.Ordinal))
        {
            var parts = value.Split(':');
            if (parts.Length != 3 || parts[1].Length is < 4 or > 6
                || !int.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)
                || !IconCatalog.Glyphs.Any(g => g.CodePoint == code)) return null;
            string color = parts[2].ToUpperInvariant();
            return IconCatalog.Colors.Any(c => c.Hex == color) ? new() { CodePoint = code, Color = color } : null;
        }
        if (!value.StartsWith("file:", StringComparison.Ordinal)) return null;
        string path = value[5..]; int? index = null;
        int comma = path.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(path.AsSpan(comma + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
        { index = number; path = path[..comma]; }
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".ico" or ".png" or ".jpg" or ".bmp" or ".exe" or ".dll")
            || (index.HasValue && extension is not (".exe" or ".dll"))) return null;
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path);
            if (!System.IO.Path.IsPathFullyQualified(expanded) || expanded.IndexOfAny(['"', '<', '>', '|', '?', '*', '\0']) >= 0
                || expanded.Any(char.IsControl)) return null;
            return new() { Path = ItemIdentity.PortablePath(expanded), Index = index };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    public override string ToString() => IsGlyph ? $"glyph:{CodePoint:X4}:{Color}"
        : "file:" + Path + (Index is { } index ? "," + index.ToString(CultureInfo.InvariantCulture) : "");
    public static string? Normalize(string? value) => Parse(value)?.ToString();
}

// Optional decoration must not make an otherwise usable configuration unreadable.
public sealed class IconValueConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        reader.Skip(); return null;
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
