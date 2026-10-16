using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Threading;

namespace QuickLaunch.Views;

public partial class EditItemDialog
{
    private sealed class PickerEntry(CatalogIcon icon) : ObservableObject
    {
        public CatalogIcon Icon => icon;
        public string Name => icon.Name;
        public string Spec => icon.Spec;
        private bool _selected;
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
    }
    private readonly CancellationTokenSource _pickerLifetime = new();
    private readonly Dictionary<Image, (CancellationTokenSource Cancellation, Task Task)> _imageLoads = [];
    private PickerEntry[]? _systemEntries, _colorEntries;
    private PickerEntry[] _entries = [];
    private int _seriesVersion;
    private int _pickerWorkVersion, _catalogPixels;
    private Task _catalogRepack = Task.CompletedTask;
    private bool _repackQueued;

    private void InitializeIconPicker()
    {
        var selection = new SelectionConverter();
        foreach (var glyph in ViewModel.Glyphs)
        {
            var choice = new RadioButton { Tag = glyph.CodePoint, GroupName = "IconGlyph", ToolTip = glyph.Name,
                Style = (Style)FindResource("IconChoice"), Command = ViewModel.SelectGlyphCommand, CommandParameter = glyph.CodePoint,
                Content = new TextBlock { Text = glyph.Text, FontFamily = new FontFamily(IconCatalog.FontName), FontSize = 18, Foreground = Brushes.White } };
            choice.SetBinding(BackgroundProperty, new Binding(nameof(EditItemViewModel.IconColor)));
            choice.SetBinding(RadioButton.IsCheckedProperty, new Binding(nameof(EditItemViewModel.SelectedGlyph))
                { Mode = BindingMode.OneWay, Converter = selection, ConverterParameter = glyph.CodePoint });
            AutomationProperties.SetName(choice, glyph.Name);
            GlyphGrid.Children.Add(choice);
        }
        foreach (var color in ViewModel.Colors)
        {
            var choice = new RadioButton { Tag = color.Hex, GroupName = "IconColor", ToolTip = color.Name,
                Style = (Style)FindResource("IconChoice"), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Hex)),
                Command = ViewModel.SelectColorCommand, CommandParameter = color.Hex };
            choice.SetBinding(RadioButton.IsCheckedProperty, new Binding(nameof(EditItemViewModel.IconColor))
                { Mode = BindingMode.OneWay, Converter = selection, ConverterParameter = color.Hex });
            AutomationProperties.SetName(choice, color.Name);
            PaletteGrid.Children.Add(choice);
        }
    }
    internal void OpenIconPicker() => ViewModel.OpenIconPickerCommand.Execute(null);
    private async void IconPopupOpened(object? sender, EventArgs e)
    {
        int series = IconSpec.Parse(ViewModel.CustomIcon) switch
        {
            { IsFluent: true } => 2,
            { Path: { } path, Index: < 0 } when SystemIconCatalog.Libraries.Contains(path, StringComparer.OrdinalIgnoreCase) => 1,
            _ => 0
        };
        await SelectIconSeriesAsync(series, focus: true);
    }
    private void FocusGlyph()
    {
        var choices = GlyphGrid.Children.OfType<RadioButton>().ToArray();
        Control focus = choices.FirstOrDefault(c => c.IsChecked == true) ?? choices.FirstOrDefault() ?? (Control)IconFileButton;
        focus.Focus(); focus.BringIntoView();
    }

    private async void SeriesClicked(object sender, RoutedEventArgs e) =>
        await SelectIconSeriesAsync(int.Parse((string)((RadioButton)sender).Tag), focus: false);

    internal async Task SelectIconSeriesAsync(int series, bool focus = false)
    {
        int version = ++_seriesVersion;
        _pickerWorkVersion++;
        _catalogPixels = IconService.PixelSize(28, VisualTreeHelper.GetDpi(this).DpiScaleX);
        SimpleSeries.SetCurrentValue(RadioButton.IsCheckedProperty, series == 0);
        SystemSeries.SetCurrentValue(RadioButton.IsCheckedProperty, series == 1);
        ColorSeries.SetCurrentValue(RadioButton.IsCheckedProperty, series == 2);
        GlyphScroller.Visibility = series == 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogRows.Visibility = series == 0 ? Visibility.Collapsed : Visibility.Visible;
        PaletteArea.Visibility = series == 0 ? Visibility.Visible : Visibility.Hidden;
        ClearCatalogImageLoads(); CatalogRows.ItemsSource = null;
        _entries = [];
        if (series == 0) { if (focus) FocusGlyph(); return; }
        try
        {
            if (series == 1 && _systemEntries is null)
                _systemEntries = (await _icons.GetSystemIconsAsync().WaitAsync(_pickerLifetime.Token)).Select(i => new PickerEntry(i)).ToArray();
            if (_closed || version != _seriesVersion) return;
            _entries = series == 1 ? _systemEntries!.Where(i => !_icons.IsEmptySystemIcon(i.Spec, _catalogPixels)).ToArray()
                : _colorEntries ??= FluentColorCatalog.Icons.Select(i => new PickerEntry(i)).ToArray();
            UpdateCatalogSelection();
            CatalogRows.ItemsSource = _entries.Chunk(8).ToArray();
            int selected = Math.Max(0, Array.FindIndex(_entries, i => i.IsSelected));
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (_closed || version != _seriesVersion || _entries.Length == 0) return;
            RevealCatalogIcon(selected, focus);
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateCatalogSelection()
    {
        foreach (var entry in _entries) entry.IsSelected = string.Equals(entry.Spec, ViewModel.CustomIcon, StringComparison.OrdinalIgnoreCase);
    }

    private void RevealCatalogIcon(int index, bool focus)
    {
        CatalogRows.ScrollIntoView(CatalogRows.Items[index / 8]);
        CatalogRows.UpdateLayout();
        LoadRealizedCatalogImages();
        if (focus)
            LauncherWindow.Descendants<RadioButton>(CatalogRows).FirstOrDefault(b => b.DataContext == _entries[index])?.Focus();
    }

    private void CatalogImageLoaded(object sender, RoutedEventArgs e)
    {
        var image = (Image)sender;
        if (_closed || image.DataContext is not PickerEntry entry || _imageLoads.ContainsKey(image)) return;
        _pickerWorkVersion++;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_pickerLifetime.Token);
        _imageLoads[image] = (cancellation, LoadCatalogImageAsync(image, entry, cancellation.Token));
    }
    private async Task LoadCatalogImageAsync(Image image, PickerEntry entry, CancellationToken cancellation)
    {
        try
        {
            bool system = entry.Spec.StartsWith("file:", StringComparison.Ordinal);
            var bitmap = system ? await _icons.GetSystemPickerImageAsync(entry.Icon, _catalogPixels, cancellation)
                : await _icons.GetAsync(new Models.LaunchItem("", "", Models.LaunchKind.File) { Icon = entry.Spec }, _catalogPixels, cancellation);
            if (!cancellation.IsCancellationRequested && image.DataContext == entry)
            {
                image.Source = bitmap;
                if (system && bitmap is null) QueueSystemRepack();
            }
        }
        catch (OperationCanceledException) { }
    }
    private void CatalogImageUnloaded(object sender, RoutedEventArgs e)
    {
        var image = (Image)sender;
        if (_imageLoads.Remove(image, out var load)) { _pickerWorkVersion++; load.Cancellation.Cancel(); load.Cancellation.Dispose(); }
        image.Source = null;
    }

    private void LoadRealizedCatalogImages()
    {
        foreach (var image in LauncherWindow.Descendants<Image>(CatalogRows))
            CatalogImageLoaded(image, new RoutedEventArgs());
    }

    private void QueueSystemRepack()
    {
        if (_closed || SystemSeries.IsChecked != true || _repackQueued) return;
        // Coalesce completions so multiple empty resources cause one row rebuild.
        _repackQueued = true;
        _catalogRepack = Dispatcher.InvokeAsync(() => { _repackQueued = false; RepackSystemIcons(); }, DispatcherPriority.Background).Task;
    }

    private void RepackSystemIcons()
    {
        if (_closed || SystemSeries.IsChecked != true) return;
        var usable = _entries.Where(i => !_icons.IsEmptySystemIcon(i.Spec, _catalogPixels)).ToArray();
        if (usable.Length == _entries.Length) return;
        var scroll = LauncherWindow.Descendants<ScrollViewer>(CatalogRows).FirstOrDefault();
        int start = Math.Clamp((int)(scroll?.VerticalOffset ?? 0) * 8, 0, _entries.Length - 1);
        var anchor = _entries.Skip(start).FirstOrDefault(usable.Contains) ?? usable.LastOrDefault();
        var focused = (Keyboard.FocusedElement as RadioButton)?.DataContext as PickerEntry;
        int focusIndex = focused is null ? -1 : Array.IndexOf(_entries, focused);
        if (focusIndex >= 0) focused = _entries.Skip(focusIndex).FirstOrDefault(usable.Contains) ?? usable.LastOrDefault();
        _entries = usable;
        _pickerWorkVersion++;
        ClearCatalogImageLoads();
        CatalogRows.ItemsSource = _entries.Chunk(8).ToArray();
        CatalogRows.UpdateLayout();
        if (anchor is not null) scroll?.ScrollToVerticalOffset(Array.IndexOf(_entries, anchor) / 8);
        CatalogRows.UpdateLayout();
        LoadRealizedCatalogImages(); // Fill newly exposed cells without waiting for a scroll gesture.
        if (focusIndex >= 0)
        {
            if (focused is not null) RevealCatalogIcon(Array.IndexOf(_entries, focused), focus: true);
            else IconFileButton.Focus();
        }
    }

    internal async Task WaitForPickerAsync()
    {
        // Repacking realizes replacement cells. A single WhenAll snapshot could finish
        // before those cells even started loading, producing holes in a captured page.
        while (!_closed)
        {
            PickerSurface.UpdateLayout();
            LoadRealizedCatalogImages();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            int version = _pickerWorkVersion;
            await Task.WhenAll(_imageLoads.Values.Select(l => l.Task).Append(_catalogRepack).ToArray());
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (version == _pickerWorkVersion && _catalogRepack.IsCompleted && _imageLoads.Values.All(l => l.Task.IsCompleted)) return;
        }
    }
    private void ClearCatalogImageLoads()
    {
        foreach (var image in _imageLoads.Keys.ToArray()) CatalogImageUnloaded(image, new RoutedEventArgs());
    }
    private void CancelPickerLoads()
    {
        _pickerLifetime.Cancel();
        foreach (var load in _imageLoads.Values) load.Cancellation.Dispose();
        _imageLoads.Clear(); _pickerLifetime.Dispose();
    }

    private async void SeriesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not RadioButton current) return;
        int index = int.Parse((string)current.Tag);
        int next = e.Key switch { Key.Left => (index + 2) % 3, Key.Right => (index + 1) % 3,
            Key.Home => 0, Key.End => 2, Key.Enter => index, _ => -1 };
        if (next < 0) return;
        e.Handled = true;
        await SelectIconSeriesAsync(next);
        ((RadioButton)IconSeries.Children[next]).Focus();
    }

    private void CatalogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not RadioButton current || current.DataContext is not PickerEntry entry) return;
        int index = Array.IndexOf(_entries, entry);
        int next = e.Key switch
        {
            Key.Left => (index + _entries.Length - 1) % _entries.Length,
            Key.Right => (index + 1) % _entries.Length,
            Key.Up => Math.Max(0, index - 8), Key.Down => Math.Min(_entries.Length - 1, index + 8),
            Key.Home => 0, Key.End => _entries.Length - 1, _ => -1
        };
        if (next >= 0) { RevealCatalogIcon(next, focus: true); e.Handled = true; }
        else if (e.Key == Key.Enter) { ExecuteChoice(current); e.Handled = true; }
    }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (IconPopup.IsOpen && e.Key == Key.Escape)
        {
            IconPopup.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false); e.Handled = true;
            Dispatcher.BeginInvoke(() => { if (!_closed && !IconPopup.IsOpen) IconPreview.Focus(); });
        }
    }
    private static void ExecuteChoice(System.Windows.Controls.Primitives.ButtonBase button)
    {
        if (button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter);
    }
    private void PickerKeyDown(object sender, KeyEventArgs e)
    {
        EditorKeyDown(sender, e);
        // Enter activates a picker choice, never the editor's default Save button.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button button)
        { ExecuteChoice(button); e.Handled = true; }
    }
    private void ChoiceKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Panel panel || e.OriginalSource is not RadioButton current) return;
        var choices = panel.Children.OfType<RadioButton>().ToArray();
        int index = Array.IndexOf(choices, current);
        int next = e.Key switch
        {
            Key.Left => (index + choices.Length - 1) % choices.Length,
            Key.Right => (index + 1) % choices.Length,
            Key.Up => Math.Max(0, index - 8), Key.Down => Math.Min(choices.Length - 1, index + 8),
            Key.Home => 0, Key.End => choices.Length - 1, _ => -1
        };
        if (next >= 0) { choices[next].Focus(); choices[next].BringIntoView(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        { ExecuteChoice(current); e.Handled = true; }
    }
}
