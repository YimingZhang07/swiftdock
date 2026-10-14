using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLaunch.Views;

public sealed class ToastSurface : Border
{
    // Keep the painted surface click-through while allowing the cancel button to receive input.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
}
