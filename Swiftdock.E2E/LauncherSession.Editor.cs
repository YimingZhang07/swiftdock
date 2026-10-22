using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Swiftdock.E2E;

internal sealed partial class LauncherSession
{
    internal void AssertEditorTypeHeight(Window dialog)
    {
        int height = dialog.BoundingRectangle.Height;
        foreach (string card in new[] { "AppCard", "FileCard", "FolderCard", "UrlCard" })
        {
            var chip = ById(card, dialog);
            Click(chip, dialog);
            Eventually(() => chip.Patterns.SelectionItem.Pattern.IsSelected.Value, card + " selected by real click");
            // Check repeatedly through the layout/animation interval; a transient jump also fails.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            do { Assert.Equal(height, dialog.BoundingRectangle.Height); Thread.Sleep(25); }
            while (watch.ElapsedMilliseconds < 250);
        }
    }

    internal AutomationElement FirstSystemIcon()
    {
        AutomationElement? found = null;
        Eventually(() => (found = ById("CatalogRows").FindAllDescendants(cf => cf.ByControlType(ControlType.RadioButton))
            .FirstOrDefault(e => !e.IsOffscreen && e.AutomationId.StartsWith("file:%SystemRoot%", StringComparison.Ordinal))) is not null,
            "a visible Windows resource icon");
        return found!;
    }

    internal void AssertSystemIconPagePacked() => Eventually(() =>
    {
        var grid = ById("CatalogRows");
        var viewport = grid.BoundingRectangle;
        var cells = grid.FindAllDescendants(cf => cf.ByControlType(ControlType.RadioButton))
            .Where(e => !e.IsOffscreen).Select(e => e.BoundingRectangle).Where(viewport.Contains)
            .OrderBy(r => r.Top).ThenBy(r => r.Left).ToArray();
        // A full eight-row viewport may clip a boundary row by one physical pixel.
        if (cells.Length < 56 || cells.GroupBy(r => r.Top).Any(row => row.Count() != 8)) return false;
        using var capture = Capture.Rectangle(viewport);
        foreach (var cell in cells)
        {
            int left = cell.Left - viewport.Left, top = cell.Top - viewport.Top;
            Color background = capture.Bitmap.GetPixel(left, top);
            int inset = (int)Math.Round(cell.Width * 4d / 36); // Exclude hover/focus/selection borders.
            int ink = 0, area = (cell.Width - 2 * inset) * (cell.Height - 2 * inset);
            for (int y = top + inset; y < top + cell.Height - inset; y++)
            for (int x = left + inset; x < left + cell.Width - inset; x++)
                if (Distance(background, capture.Bitmap.GetPixel(x, y)) >= 20) ink++;
            if (ink < Math.Max(4, area / 100)) return false;
        }
        return true;
    }, "system viewport to fill with packed, visible icons and no empty cells");

    internal void AssertPersistedIcon(string name, string spec) => Eventually(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigFile));
        return document.RootElement.GetProperty("groups")[0].GetProperty("items").EnumerateArray()
            .Any(i => i.GetProperty("name").GetString() == name && i.TryGetProperty("icon", out var icon) && icon.GetString() == spec);
    }, $"{name} persisted icon {spec}");

    internal void AssertTileMatchesIcon(string name, string spec, string? previousSpec = null)
    {
        // Save selects the new/edited tile. Remove its accent surface and keep the
        // pointer in the panel's empty bottom strip for the entire capture interval.
        if (Tiles().Any(tile => tile.Patterns.SelectionItem.Pattern.IsSelected.Value)) Key(VirtualKeyShort.ESCAPE);
        AssertSelection(-1, "icon comparison");
        var panel = Panel.BoundingRectangle;
        var pointer = new Point(panel.Right - (int)(24 * Scale), panel.Bottom - (int)(24 * Scale));
        Assert.DoesNotContain(Tiles(), tile => tile.BoundingRectangle.Contains(pointer));
        SaveCursor(); MoveMouse(pointer, "clear tile hover for icon comparison");
        var references = new Dictionary<string, IconImageMatcher.Raster>();
        string diagnostic = "Waiting for the app's tile-size PNG cache.";
        try
        {
            Eventually(() =>
            {
                var tile = Tiles().Single(t => t.Name == name);
                var bounds = ById("TileImage", tile).BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Width != bounds.Height) return false;
                string file = IconImageMatcher.CacheFileName(spec, bounds.Width);
                string? previous = previousSpec is null ? null : IconImageMatcher.CacheFileName(previousSpec, bounds.Width);
                foreach (string needed in previous is null ? new[] { file } : new[] { file, previous })
                {
                    if (references.ContainsKey(needed)) continue;
                    string path = Path.Combine(_temp, "diagnostics", "icons", needed);
                    if (!File.Exists(path)) { diagnostic = "Missing rendered reference: " + path; return false; }
                    using var bitmap = new Bitmap(path);
                    references.Add(needed, IconPixels(bitmap));
                }
                var padded = bounds; padded.Inflate(IconImageMatcher.Padding, IconImageMatcher.Padding);
                using var capture = Capture.Rectangle(padded);
                var pixels = IconPixels(capture.Bitmap);
                var chosen = IconImageMatcher.Compare(references[file], pixels);
                var stale = previous is null ? null : IconImageMatcher.Compare(references[previous], pixels);
                diagnostic = $"{name}, {spec}, {bounds.Width}px: chosen [{chosen}], previous [{stale}], reference={file}";
                return chosen.Matches && stale?.Matches != true;
            }, name + " tile to match the chosen rendered icon, rather than the placeholder or previous icon");
        }
        finally { Diagnostic(diagnostic); }
    }

    private static IconImageMatcher.Raster IconPixels(Bitmap bitmap)
    {
        byte[] bytes = new byte[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            int i = (y * bitmap.Width + x) * 4;
            Color pixel = bitmap.GetPixel(x, y);
            bytes[i] = pixel.B; bytes[i + 1] = pixel.G; bytes[i + 2] = pixel.R; bytes[i + 3] = pixel.A;
        }
        return new(bitmap.Width, bitmap.Height, bytes);
    }

    private static int Distance(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
}
