using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using Xunit;

namespace Swiftdock.E2E;

internal sealed partial class LauncherSession
{
    internal sealed record PanelFrame(nint Handle, Rectangle Bounds, FrameDetector.Sample Sample);

    public PanelFrame CapturePanelFrame(string phase)
    {
        AssertForeground();
        var frame = ReadPanelFrame(PanelHandle);
        Diagnostic($"{phase}: {frame.Bounds}; {frame.Sample}");
        Assert.False(frame.Sample.HasFrame, "Panel already has a light frame before the dialog: " + frame.Sample);
        return frame;
    }

    private static PanelFrame ReadPanelFrame(nint handle)
    {
        Assert.Equal(handle, Desktop.GetForegroundWindow());
        Assert.True(Desktop.GetWindowRect(handle, out var nativeBounds));
        using var capture = Capture.Rectangle(nativeBounds.Rectangle);
        var bitmap = capture.Bitmap;
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        int corner = Math.Max(24, (int)Math.Ceiling(16 * Desktop.GetDpiForWindow(handle) / 96.0));
        return new(handle, nativeBounds.Rectangle, FrameDetector.Analyze(pixels, bitmap.Width, bitmap.Height, corner));
    }

    public void AssertPanelFrameUnchanged(PanelFrame before, string phase)
    {
        PanelFrame? after = null;
        var stable = Stopwatch.StartNew();
        try
        {
            Eventually(() =>
            {
                if (Desktop.GetForegroundWindow() != before.Handle) { stable.Restart(); return false; }
                after = ReadPanelFrame(before.Handle);
                Assert.Equal(before.Bounds, after.Bounds);
                bool clean = !after.Sample.HasFrame && after.Sample.IncreaseFrom(before.Sample) <= .1;
                if (!clean) stable.Restart();
                return clean && stable.ElapsedMilliseconds >= 350;
            }, phase + ": no classic frame after owned dialog closes", 3000);
        }
        finally { Diagnostic($"{phase}: before [{before.Sample}]; after [{after?.Sample.ToString() ?? "no foreground sample"}]"); }
    }
}
