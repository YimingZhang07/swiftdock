using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Swiftdock.Services;
using Swiftdock.ViewModels;

namespace Swiftdock.Views;

public partial class EditItemDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IconService _icons;
    private CancellationTokenSource? _preview;
    private Task _previewRefresh = Task.CompletedTask;
    private bool _closed;
    internal bool OpenPickerOnLoad { get; set; }
    public EditItemViewModel ViewModel { get; }
    internal Task TargetRefresh => WaitForPreviewAsync();

    public EditItemDialog(EditItemViewModel viewModel, IconService icons, DialogFilePicker picker)
    {
        ViewModel = viewModel; _icons = icons; picker.Owner = this;
        InitializeComponent(); DataContext = viewModel;
        UrlDescription.Text = icons.AutoFetchWebsiteIcons ? "添加后会自动获取网站图标。" : "自动获取网站图标已关闭，可手动选择图标。";
        InitializeIconPicker();
        viewModel.PropertyChanged += ModelChanged;
        viewModel.Accepted += Accept;
        Closed += (_, _) =>
        {
            _closed = true; IconPopup.IsOpen = false; CancelPreview(); CancelPickerLoads();
            viewModel.PropertyChanged -= ModelChanged; viewModel.Accepted -= Accept;
            picker.Owner = null; viewModel.Dispose();
        };
        Loaded += (_, _) =>
        {
            viewModel.RefreshPreview(); NameBox.Focus(); NameBox.SelectAll();
            if (OpenPickerOnLoad) Dispatcher.BeginInvoke(OpenIconPicker);
        };
    }
    private void Accept() => DialogResult = true;
    private async Task WaitForPreviewAsync()
    { await ViewModel.TargetRefresh; await _previewRefresh; }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditItemViewModel.PreviewTarget)) QueuePreview();
        if (e.PropertyName == nameof(EditItemViewModel.CustomIcon)) UpdateCatalogSelection();
        if (e.PropertyName == nameof(EditItemViewModel.IsSaving) && ViewModel.IsSaving) CancelPreview();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsInitialized) QueuePreview();
    }
    private void TypeKeyDown(object sender, KeyEventArgs e)
    {
        if (!ViewModel.IsEditing || e.OriginalSource is not RadioButton current || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var cards = TypeCards.Children.OfType<RadioButton>().ToArray();
        int step = e.Key is Key.Left or Key.Up ? -1 : 1;
        var next = cards[(Array.IndexOf(cards, current) + step + cards.Length) % cards.Length];
        ExecuteChoice(next); next.Focus(); e.Handled = true;
    }
    private void CancelPreview() { _preview?.Cancel(); _preview?.Dispose(); _preview = null; }
    private void QueuePreview()
    {
        CancelPreview(); PreviewImage.Source = null; PreviewGlyph.Visibility = Visibility.Visible;
        if (_closed || ViewModel.PreviewTarget is not { } target) return;
        _preview = new();
        _previewRefresh = RenderPreviewAsync(target, _preview.Token);
    }
    private async Task RenderPreviewAsync(Models.LaunchItem target, CancellationToken cancellation)
    {
        try
        {
            int pixels = IconService.PixelSize(40, VisualTreeHelper.GetDpi(this).DpiScaleX);
            var image = IconService.UsesFavicon(target) ? IconService.Globe(pixels) : await _icons.GetAsync(target, pixels, cancellation);
            cancellation.ThrowIfCancellationRequested();
            PreviewImage.Source = image; PreviewGlyph.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            if (IconService.UsesFavicon(target))
            {
                var favicon = await _icons.GetFaviconAsync(target, pixels, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (_icons.AutoFetchWebsiteIcons && favicon is not null) PreviewImage.Source = favicon;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException or IOException or UnauthorizedAccessException)
        { /* Unavailable images keep the kind glyph visible. */ }
    }
}
