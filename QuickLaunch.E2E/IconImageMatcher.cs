using System.Security.Cryptography;
using System.Text;

namespace QuickLaunch.E2E;

// Shared with WPF-host tests. No desktop, image decoder or product dependency.
internal static class IconImageMatcher
{
    internal const int Padding = 2;
    internal sealed record Raster(int Width, int Height, byte[] Bgra);
    internal sealed record Result(double Similarity, double InkError, double ClearError, int InkPixels, int X, int Y)
    {
        // Calibrated with the actual apps_list renderer at 32/64px, both themes and
        // half-pixel positioning: correct ink RMS <= 24.2, wrong icons >= 63.0.
        internal bool Matches => InkPixels >= 8 && Similarity >= .90 && InkError <= 28 && ClearError <= 12;
        public override string ToString() => $"match={Matches}, correlation={Similarity:F3}, ink RMS={InkError:F1}, clear RMS={ClearError:F1}, ink pixels={InkPixels}, offset=({X},{Y})";
    }

    // The scenario uses URL items. Unit tests verify these names against PNGs actually
    // written by IconService, so a cache-format change fails before desktop execution.
    internal static string CacheFileName(string? spec, int pixels)
    {
        string source = spec is null ? "v1:globe"
            : spec.StartsWith("fluent:", StringComparison.Ordinal) ? "fluent-color-1.1.342-v1:" + spec[7..]
            : "v1:globe|custom-v1:" + spec + "|file";
        return $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}-{pixels}.png";
    }

    internal static Result Compare(Raster reference, Raster capture)
    {
        int width = reference.Width, height = reference.Height, stride = capture.Width;
        if (capture.Width != width + 2 * Padding || capture.Height != height + 2 * Padding
            || reference.Bgra.Length != width * height * 4 || capture.Bgra.Length != stride * capture.Height * 4)
            throw new ArgumentException("Expected a tile-size BGRA reference and a capture with two pixels of surrounding solid surface.");

        // Read the surrounding surface, never a guessed transparent pixel inside the icon.
        double[] background = Enumerable.Range(0, 3).Select(channel =>
        {
            List<byte> border = [];
            for (int x = 0; x < stride; x++)
            { border.Add(capture.Bgra[x * 4 + channel]); border.Add(capture.Bgra[((capture.Height - 1) * stride + x) * 4 + channel]); }
            for (int y = 1; y < capture.Height - 1; y++)
            { border.Add(capture.Bgra[y * stride * 4 + channel]); border.Add(capture.Bgra[(y * stride + stride - 1) * 4 + channel]); }
            border.Sort(); return (double)border[border.Count / 2];
        }).ToArray();
        double[] expected = new double[capture.Bgra.Length], actual = new double[capture.Bgra.Length];
        for (int p = 0; p < expected.Length; p += 4)
        for (int c = 0; c < 3; c++) { expected[p + c] = background[c]; actual[p + c] = capture.Bgra[p + c]; }
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int source = (y * width + x) * 4, target = ((y + Padding) * stride + x + Padding) * 4;
            double alpha = reference.Bgra[source + 3] / 255d;
            for (int c = 0; c < 3; c++) expected[target + c] = alpha * reference.Bgra[source + c] + (1 - alpha) * background[c];
            expected[target + 3] = alpha;
        }
        // A one-physical-pixel tent filter tolerates rasterization/antialiasing differences
        // without discarding thin strokes. Every pixel participates; no sparse grid.
        expected = Blur(expected, stride, capture.Height); actual = Blur(actual, stride, capture.Height);
        Result? best = null;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            double dot = 0, expectedEnergy = 0, actualEnergy = 0, inkError = 0, clearError = 0;
            int ink = 0, clear = 0;
            for (int y = Padding; y < height + Padding; y++)
            for (int x = Padding; x < width + Padding; x++)
            {
                int e = (y * stride + x) * 4, a = ((y + dy) * stride + x + dx) * 4;
                double alpha = expected[e + 3];
                if (alpha >= .2) ink++;
                if (alpha <= .01) clear++;
                for (int c = 0; c < 3; c++)
                {
                    double ec = expected[e + c] - background[c], ac = actual[a + c] - background[c];
                    dot += ec * ac; expectedEnergy += ec * ec; actualEnergy += ac * ac;
                    double error = Math.Pow(expected[e + c] - actual[a + c], 2);
                    if (alpha >= .2) inkError += error;
                    if (alpha <= .01) clearError += error;
                }
            }
            var result = new Result(expectedEnergy > 0 && actualEnergy > 0 ? dot / Math.Sqrt(expectedEnergy * actualEnergy) : 0,
                ink == 0 ? double.PositiveInfinity : Math.Sqrt(inkError / (3 * ink)),
                clear == 0 ? double.PositiveInfinity : Math.Sqrt(clearError / (3 * clear)), ink, dx, dy);
            // One global registration offset, never a different best neighbour for each pixel.
            if (best is null || result.Matches && !best.Matches || result.Matches == best.Matches && result.Similarity > best.Similarity) best = result;
        }
        return best!;
    }

    private static double[] Blur(double[] source, int width, int height)
    {
        double[] result = (double[])source.Clone();
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
        for (int c = 0; c < 4; c++)
        {
            double sum = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                sum += source[((y + dy) * width + x + dx) * 4 + c] * (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1);
            result[(y * width + x) * 4 + c] = sum / 16;
        }
        return result;
    }
}
