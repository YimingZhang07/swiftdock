using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickLaunch.ViewModels;

// Runtime-only presentation state: updating an image must not replace items or save config.
public sealed class IconVisual : ObservableObject
{
    private BitmapSource? _image;
    public BitmapSource? Image { get => _image; set => SetProperty(ref _image, value); }
    public int RequestedPixels { get; private set; }
    private int _generation;
    public int BeginRequest(int pixels) { RequestedPixels = pixels; return ++_generation; }
    public void Complete(int generation, BitmapSource? image)
    { if (generation == _generation) { Image = image; if (image is null) RequestedPixels = 0; } }
}
