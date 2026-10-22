using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using Swiftdock.Models;
using Swiftdock.Services;
using Swiftdock.ViewModels;
using Swiftdock.Views;
using Wpf.Ui.Appearance;
using Xunit;

namespace Swiftdock.Tests;

[Collection("WPF")]
public sealed class EditItemLayoutTests
{
    [Theory]
    [InlineData(1d)] [InlineData(2d)]
    public Task PickerKeepsItsSizeAndRealizesOnlyVisibleCatalogRows(double scale) => WpfTestHost.RunAsync(async () =>
    {
        await using var log = new TestLogging();
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-picker-" + Guid.NewGuid().ToString("N"));
        using var icons = log.CreateIcons(directory);
        var picker = new DialogFilePicker();
        using var model = new EditItemViewModel(picker, new ItemEditorEnvironment());
        model.Initialize(null);
        var dialog = new EditItemDialog(model, icons, picker);
        try
        {
            VisualTreeHelper.SetRootDpi(dialog, new DpiScale(scale, scale));
            var surface = dialog.PickerSurface;
            dialog.IconPopup.Child = null; surface.DataContext = model;
            Size Measure()
            {
                surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                surface.Arrange(new Rect(surface.DesiredSize)); surface.UpdateLayout();
                return surface.DesiredSize;
            }
            var size = Measure();
            foreach (int series in new[] { 1, 2, 0 })
            {
                var entries = series == 1 ? await icons.GetSystemIconsAsync() : FluentColorCatalog.Icons;
                string? selected = entries.Count == 0 ? null : entries[series == 1 ? 0 : ^1].Spec;
                if (series != 0) model.CustomIcon = selected;
                await dialog.SelectIconSeriesAsync(series);
                Assert.Equal(size, Measure());
                await dialog.WaitForPickerAsync();
                if (series == 0 || entries.Count == 0) continue;
                var choices = LauncherWindow.Descendants<RadioButton>(dialog.CatalogRows).ToArray();
                Assert.InRange(choices.Length, 64, 80); // Eight viewport rows, at most two boundary rows.
                Assert.True(choices.Length < entries.Count);
                Assert.Single(choices, c => c.IsChecked == true && (string)c.Tag == selected);
                Assert.Equal(Visibility.Hidden, dialog.PaletteArea.Visibility);
                Assert.Equal(dialog.PickerGridArea.ActualHeight, dialog.CatalogRows.ActualHeight);
                Assert.All(choices, c => Assert.True(IconService.HasVisiblePixels((BitmapSource?)((Image)c.Content).Source), (string)c.Tag));
                if (series == 1)
                {
                    int pixels = IconService.PixelSize(28, VisualTreeHelper.GetDpi(dialog).DpiScaleX);
                    Assert.Equal(entries.Where(i => !icons.IsEmptySystemIcon(i.Spec, pixels)).Take(choices.Length).Select(i => i.Spec),
                        choices.Select(c => (string)c.Tag));
                    long extractions = icons.Extractions;
                    await dialog.SelectIconSeriesAsync(1);
                    await dialog.WaitForPickerAsync();
                    Assert.Equal(extractions, icons.Extractions); // Includes cached rejections and replacement cells.
                }
            }
        }
        finally { dialog.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task CollapsedEditorHeightIsStableAcrossTypesModesThemesAndDpi() => WpfTestHost.RunAsync(async () =>
    {
        await using var log = new TestLogging();
        using var icons = log.CreateIcons(Path.Combine(Path.GetTempPath(), "Swiftdock-layout-" + Guid.NewGuid().ToString("N")));
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (var theme in new[] { ApplicationTheme.Dark, ApplicationTheme.Light })
        foreach (double scale in new[] { 1d, 2d })
        foreach (bool edit in new[] { false, true })
        {
            Swiftdock.Services.Localization.Instance.Apply(language);
            ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: false);
            var picker = new DialogFilePicker();
            using var model = new EditItemViewModel(picker, new ItemEditorEnvironment());
            model.Initialize(edit ? new("Example", @"C:\example.exe", LaunchKind.App, "--example") : null);
            icons.AutoFetchWebsiteIcons = edit;
            var dialog = new EditItemDialog(model, icons, picker);
            try
            {
                // Measure the actual content without showing a window or starting product services.
                var content = (FrameworkElement)dialog.Content;
                VisualTreeHelper.SetRootDpi(dialog, new DpiScale(scale, scale));
                double Measure()
                {
                    content.InvalidateMeasure(); content.Measure(new Size(520, double.PositiveInfinity));
                    content.Arrange(new Rect(content.DesiredSize)); content.UpdateLayout();
                    return content.DesiredSize.Height;
                }
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                double height = Measure();
                Assert.True(height > 300); Assert.False(model.AdvancedExpanded);
                Assert.Equal(Swiftdock.Services.Localization.Instance[edit ? "TheWebsiteIconWillBeFetchedAfterAdding" : "AutoFetchIsOffChooseAnIconManually"], dialog.UrlDescription.Text);
                foreach (var kind in Enum.GetValues<LaunchKind>())
                {
                    model.SelectTypeCommand.Execute(kind);
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    Assert.Equal(height, Measure());
                    Assert.Equal(54, dialog.TypeArea.ActualHeight);
                    model.Error = new string('错', 200);
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    Assert.Equal(height, Measure());
                }
                model.SelectedKind = LaunchKind.App; model.AdvancedExpanded = true;
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                await Task.Delay(240); // CardExpander's visibility keyframe is applied on a timing tick.
                double expanded = Measure();
                Assert.True(expanded > height, $"theme={theme}, scale={scale}, edit={edit}, collapsed={height}, expanded={expanded}, area={dialog.TypeArea.Height}, expander={dialog.AdvancedOptions.IsExpanded}/{dialog.AdvancedOptions.Visibility}/{dialog.AdvancedOptions.ActualHeight}");
                model.AdvancedExpanded = false;
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(height, Measure());
            }
            finally { dialog.Close(); }
        }
    });
}
