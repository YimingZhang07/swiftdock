using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickLaunch.Views;

// Explicit binding sources also reach Gong previews, which live outside the panel tree.
// Only LauncherWindow owns this resource; dialogs and scrollbars keep Windows metrics.
public sealed class PanelResources : ObservableObject
{
    private double _factor = 1;
    public double Factor
    {
        get => _factor;
        set { if (SetProperty(ref _factor, value)) OnPropertyChanged(string.Empty); }
    }
    public Thickness Inset0_0_0_2 => new Thickness(0, 0, 0, 2 * Factor);
    public Thickness Inset0_0_0_3 => new Thickness(0, 0, 0, 3 * Factor);
    public Thickness Inset0_0_8_0 => new Thickness(0, 0, 8 * Factor, 0);
    public Thickness Inset0_6_0_0 => new Thickness(0, 6 * Factor, 0, 0);
    public Thickness Inset1 => new Thickness(Factor);
    public Thickness Inset10 => new Thickness(10 * Factor);
    public Thickness Inset10_0_0_0 => new Thickness(10 * Factor, 0, 0, 0);
    public Thickness Inset10_6 => new Thickness(10 * Factor, 6 * Factor, 10 * Factor, 6 * Factor);
    public Thickness Inset16_0 => new Thickness(16 * Factor, 0, 16 * Factor, 0);
    public Thickness Inset2 => new Thickness(2 * Factor);
    public Thickness Inset4 => new Thickness(4 * Factor);
    public Thickness Inset8_0_0_0 => new Thickness(8 * Factor, 0, 0, 0);
    public double Length1 => Factor;
    public double Length10 => 10 * Factor;
    public double Length12 => 12 * Factor;
    public double Length120 => 120 * Factor;
    public double Length13 => 13 * Factor;
    public double Length14 => 14 * Factor;
    public double Length190 => 190 * Factor;
    public double Length28 => 28 * Factor;
    public double Length3 => 3 * Factor;
    public double Length32 => 32 * Factor;
    public double Length40 => 40 * Factor;
    public double Length8 => 8 * Factor;
    public CornerRadius Radius1p5 => new CornerRadius(1.5 * Factor);
    public CornerRadius Radius4 => new CornerRadius(4 * Factor);
    public CornerRadius Radius6 => new CornerRadius(6 * Factor);
    public CornerRadius Radius8 => new CornerRadius(8 * Factor);
    public double Length24 => 24 * Factor;
    public GridLength HeaderRowHeight => new GridLength(48 * Factor);
    public Thickness ContentMargin => new Thickness(16 * Factor, 16 * Factor, 16 * Factor, 24 * Factor);
}
