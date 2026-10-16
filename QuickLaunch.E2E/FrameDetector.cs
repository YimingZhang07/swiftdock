namespace QuickLaunch.E2E;

// Shared with the unit project: no HWND, image decoder, screen capture or UIA dependency.
internal static class FrameDetector
{
    internal sealed record Band(string Side, double BrightFraction, double EdgeLuma, double InnerLuma)
    {
        public override string ToString() => $"{Side}: bright={BrightFraction:P1}, edge={EdgeLuma:F1}, inner={InnerLuma:F1}";
    }

    internal sealed record Sample(Band[] Bands)
    {
        public bool HasFrame => Bands.Count(band => band.BrightFraction >= .2) >= 2;
        public double IncreaseFrom(Sample baseline) => Bands.Zip(baseline.Bands,
            (after, before) => after.BrightFraction - before.BrightFraction).Max();
        public override string ToString() => string.Join("; ", Bands.Select(band => band.ToString()));
    }

    internal static Sample Analyze(ReadOnlySpan<byte> bgra, int width, int height, int corner = 32)
    {
        // Insets 0-1 are the normal border even in the failing reviewer capture.
        // Compare a six-device-pixel band just inside it with the unchanged interior.
        const int inset = 2, depth = 6, innerInset = 18;
        if (width <= 2 * corner || height <= 2 * corner || bgra.Length != width * height * 4)
            throw new ArgumentException("Expected a BGRA frame with straight edges outside the corner inset.");
        var bands = new List<Band>();
        foreach (string side in new[] { "top", "right", "bottom", "left" })
        {
            bool horizontal = side is "top" or "bottom";
            int length = horizontal ? width : height;
            int count = (length - 2 * corner) * depth, bright = 0;
            double edgeLuma = 0, innerLuma = 0;
            for (int offset = corner; offset < length - corner; offset++)
            for (int row = 0; row < depth; row++)
            {
                int Pixel(int d) => side switch
                {
                    "top" => (d * width + offset) * 4,
                    "right" => (offset * width + width - 1 - d) * 4,
                    "bottom" => ((height - 1 - d) * width + offset) * 4,
                    _ => (offset * width + d) * 4
                };
                int e = Pixel(inset + row), i = Pixel(innerInset + row);
                double edge = Luma(bgra, e), inner = Luma(bgra, i);
                edgeLuma += edge; innerLuma += inner;
                if (Math.Min(bgra[e], Math.Min(bgra[e + 1], bgra[e + 2])) >= 210 && edge - inner >= 50) bright++;
            }
            bands.Add(new(side, bright / (double)count, edgeLuma / count, innerLuma / count));
        }
        return new(bands.ToArray());
    }

    private static double Luma(ReadOnlySpan<byte> bgra, int index) =>
        .0722 * bgra[index] + .7152 * bgra[index + 1] + .2126 * bgra[index + 2];
}
