using System.Windows.Controls;
using System.Windows.Media;

namespace Swiftdock.Views;

public sealed class ToastSurface : Border
{
    // Keep the status line click-through while allowing the cancel link to receive input.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
}
