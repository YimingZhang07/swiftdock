using System.Windows.Media;

namespace Swiftdock.Services;

public sealed record LibraryGlyph(int CodePoint, UiText Label, UiText Category)
{
    public string Name => Label.Value;
    public string Group => Category.Value;
    public string Text => char.ConvertFromUtf32(CodePoint);
}
public sealed record IconColor(UiText Label, string Hex) { public string Name => Label.Value; }

public static class IconCatalog
{
    // Windows font glyphs only; configuration membership never depends on the installed font.
    public static IReadOnlyList<LibraryGlyph> Glyphs { get; } = Array.AsReadOnly<LibraryGlyph>([
        new(0xE943, L.Text("Code"), L.Text("AppsDevelopment")), new(0xE977, L.Text("DeviceConnection"), L.Text("AppsDevelopment")),
        new(0xE756, L.Text("Terminal"), L.Text("AppsDevelopment")), new(0xEE94, L.Text("Database"), L.Text("AppsDevelopment")),
        new(0xE968, L.Text("Server"), L.Text("AppsDevelopment")), new(0xE71D, L.Text("App"), L.Text("AppsDevelopment")),
        new(0xE950, L.Text("Devices"), L.Text("AppsDevelopment")), new(0xEBE8, L.Text("Debug"), L.Text("AppsDevelopment")),
        new(0xE774, L.Text("Web"), L.Text("Network")), new(0xE701, L.Text("WiFi"), L.Text("Network")),
        new(0xE753, L.Text("Cloud"), L.Text("Network")),
        new(0xE896, L.Text("Downloads"), L.Text("Network")), new(0xE898, L.Text("Upload"), L.Text("Network")), new(0xE71B, L.Text("Link"), L.Text("Network")),
        new(0xE715, L.Text("Mail"), L.Text("Communication")), new(0xE8F2, L.Text("Chat"), L.Text("Communication")),
        new(0xE717, L.Text("Phone"), L.Text("Communication")), new(0xE714, L.Text("VideoMeeting"), L.Text("Communication")), new(0xE716, L.Text("Contacts"), L.Text("Communication")),
        new(0xE8D6, L.Text("Music"), L.Text("Media")), new(0xE8B2, L.Text("Video"), L.Text("Media")), new(0xEB9F, L.Text("Pictures"), L.Text("Media")),
        new(0xE722, L.Text("Camera"), L.Text("Media")), new(0xE7FC, L.Text("Games"), L.Text("Media")), new(0xE768, L.Text("Play"), L.Text("Media")),
        new(0xE7F6, L.Text("Headphones"), L.Text("Media")), new(0xE720, L.Text("Microphone"), L.Text("Media")),
        new(0xE8A5, L.Text("Documents"), L.Text("Office")), new(0xE70B, L.Text("Notes"), L.Text("Office")), new(0xE80A, L.Text("Table"), L.Text("Office")),
        new(0xE787, L.Text("Calendar"), L.Text("Office")), new(0xE917, L.Text("Clock"), L.Text("Office")), new(0xE8EF, L.Text("Calculator"), L.Text("Office")),
        new(0xE82D, L.Text("Book"), L.Text("Office")), new(0xE749, L.Text("Print"), L.Text("Office")), new(0xE73E, L.Text("ToDo"), L.Text("Office")),
        new(0xE8B7, L.Text("Folder"), L.Text("File")), new(0xE7B8, L.Text("Archive"), L.Text("File")), new(0xE728, L.Text("Favorite"), L.Text("File")),
        new(0xE735, L.Text("Star"), L.Text("File")), new(0xE8EC, L.Text("Tag"), L.Text("File")), new(0xE723, L.Text("Attachment"), L.Text("File")),
        new(0xE713, L.Text("Settings"), L.Text("System")), new(0xE90F, L.Text("Tools"), L.Text("System")), new(0xEA18, L.Text("Shield"), L.Text("System")),
        new(0xE72E, L.Text("Lock"), L.Text("System")), new(0xE8D7, L.Text("Key"), L.Text("System")), new(0xE7E8, L.Text("Power"), L.Text("System")),
        new(0xE721, L.Text("Search"), L.Text("System")), new(0xE80F, L.Text("Home"), L.Text("System")),
        new(0xE7BF, L.Text("Shopping"), L.Text("Life")), new(0xE8C7, L.Text("Wallet"), L.Text("Life")), new(0xE800, L.Text("Map"), L.Text("Life")),
        new(0xE706, L.Text("Weather"), L.Text("Life")), new(0xEB52, L.Text("Heart"), L.Text("Life")), new(0xE7C1, L.Text("Flag"), L.Text("Life")),
        new(0xEA80, L.Text("Lightbulb"), L.Text("Life")), new(0xF133, L.Text("Gift"), L.Text("Life")), new(0xE707, L.Text("Location"), L.Text("Life")),
        new(0xE709, L.Text("Travel"), L.Text("Life"))
    ]);

    public static IReadOnlyList<IconColor> Colors { get; } = Array.AsReadOnly<IconColor>([
        new(L.Text("Blue"), "#2F6FDB"), new(L.Text("Teal"), "#0F7B6C"), new(L.Text("Purple"), "#5B4FD1"), new(L.Text("Orange"), "#C2410C"),
        new(L.Text("Amber"), "#B7791F"), new(L.Text("Pink"), "#B83280"), new(L.Text("Green"), "#3F7D20"), new(L.Text("Gray"), "#4B5563")
    ]);

    private static readonly Lazy<(string Name, GlyphTypeface? Face)> Font = new(() =>
        FindTypeface("Segoe Fluent Icons") is { } fluent ? ("Segoe Fluent Icons", fluent)
        : ("Segoe MDL2 Assets", FindTypeface("Segoe MDL2 Assets")));
    public static string FontName => Font.Value.Name;
    public static GlyphTypeface? Typeface => Font.Value.Face;
    public static IEnumerable<LibraryGlyph> AvailableGlyphs => Glyphs.Where(g => Typeface?.CharacterToGlyphMap.ContainsKey(g.CodePoint) == true);

    public static GlyphTypeface? FindTypeface(string name)
    {
        // Looking up an absent FontFamily by name alone can silently substitute a different font.
        var family = Fonts.SystemFontFamilies.FirstOrDefault(f => f.Source.Equals(name, StringComparison.OrdinalIgnoreCase)
            || f.FamilyNames.Values.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)));
        return family?.GetTypefaces().Select(t => t.TryGetGlyphTypeface(out var face) ? face : null).FirstOrDefault(f => f is not null);
    }
}
