using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Swiftdock.Views;

namespace Swiftdock.Snapshots;

internal static class SettingsTextContrast
{
    internal static IReadOnlyList<string> FindFailures(SettingsPage page)
    {
        page.UpdateLayout();
        var pageBrush = (SolidColorBrush)page.FindResource("NavigationViewContentBackground");
        var background = Over(pageBrush, ((SolidColorBrush)page.FindResource("ApplicationBackgroundBrush")).Color);
        var failures = new List<string>();
        foreach (var text in LauncherWindow.Descendants<TextBlock>(page))
        {
            if (string.IsNullOrWhiteSpace(text.Text) || text.ActualWidth <= 0 || text.ActualHeight <= 0) continue;
            double opacity = 1;
            for (DependencyObject? parent = text; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is UIElement element)
                    opacity *= element.Visibility == Visibility.Visible ? element.Opacity : 0;
                if (ReferenceEquals(parent, page)) break;
            }
            if (opacity <= 0 || !text.TransformToAncestor(page).TransformBounds(new Rect(text.RenderSize))
                .IntersectsWith(new Rect(page.RenderSize))) continue;
            if (text.Foreground is not SolidColorBrush foreground)
            { failures.Add($"{text.Text}: non-solid foreground cannot be checked"); continue; }
            // Page text is checked against the page. Template text (notably black
            // labels/checkmarks on accent fills) must use its painted background.
            var surfaces = new Stack<SolidColorBrush>();
            for (var parent = VisualTreeHelper.GetParent(text); parent is not null && parent != page; parent = VisualTreeHelper.GetParent(parent))
                if ((parent switch { Border border => border.Background, Panel panel => panel.Background, _ => null }) is SolidColorBrush brush)
                    surfaces.Push(brush);
            Color surface = background;
            foreach (var brush in surfaces) surface = Over(brush, surface);
            double ratio = Contrast(foreground.Color, surface, opacity * foreground.Opacity);
            if (ratio < 3)
                failures.Add($"{text.Name} [{text.Text}]: foreground {foreground.Color}, background {surface} (page {background}), contrast {ratio:F2}:1 < 3:1");
        }
        return failures;
    }

    private static Color Over(SolidColorBrush brush, Color background)
    {
        double alpha = brush.Color.A / 255.0 * brush.Opacity;
        byte Channel(byte ink, byte surface) => (byte)Math.Round(ink * alpha + surface * (1 - alpha));
        return Color.FromRgb(Channel(brush.Color.R, background.R), Channel(brush.Color.G, background.G), Channel(brush.Color.B, background.B));
    }

    internal static double Contrast(Color foreground, Color background, double opacity = 1)
    {
        double alpha = foreground.A / 255.0 * opacity;
        static double Linear(double channel) => channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        static double Luminance(double r, double g, double b) => .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
        double surface = Luminance(background.R / 255.0, background.G / 255.0, background.B / 255.0);
        double ink = Luminance((foreground.R * alpha + background.R * (1 - alpha)) / 255.0,
            (foreground.G * alpha + background.G * (1 - alpha)) / 255.0,
            (foreground.B * alpha + background.B * (1 - alpha)) / 255.0);
        return (Math.Max(surface, ink) + .05) / (Math.Min(surface, ink) + .05);
    }
}
