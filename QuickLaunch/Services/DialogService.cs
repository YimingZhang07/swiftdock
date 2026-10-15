using System.Windows;
using System.Windows.Automation;
using Microsoft.Extensions.DependencyInjection;
using QuickLaunch.Models;
using QuickLaunch.ViewModels;
using QuickLaunch.Views;

namespace QuickLaunch.Services;

public interface IDialogService
{
    bool HasOpenEditor { get; }
    bool ActivateEditor();
    bool ActivateSettings();
    void ShowSettings(LauncherWindow owner);
    void CloseSettings();
    void CloseEditor();
    LaunchItem? EditItem(LauncherWindow owner, LaunchItem? item, bool openIconPicker = false);
    string? Name(LauncherWindow owner, string title, string value);
    Task<bool> ConfirmDeleteGroupAsync(LauncherWindow owner, string name);
    bool ShowEditor(LauncherWindow owner, Window editor);
}

internal sealed class DialogService(IServiceProvider services) : IDialogService
{
    private Window? _editor;
    private SettingsWindow? _settings;
    public bool HasOpenEditor => _editor?.IsVisible == true;
    public bool ActivateEditor()
    {
        if (!HasOpenEditor) return false;
        _editor!.Activate(); return true;
    }
    public bool ActivateSettings()
    {
        if (_settings is null) return false;
        _settings.Activate(); return true;
    }
    public void CloseEditor() => _editor?.Close();
    public void CloseSettings() => _settings?.Close();

    public void ShowSettings(LauncherWindow owner)
    {
        if (ActivateEditor() || ActivateSettings()) return;
        if (!owner.IsVisible) owner.ShowPanel("settings");
        var scope = services.CreateScope();
        SettingsWindow? window = null;
        bool counted = false;
        void Release()
        {
            if (!counted) return;
            counted = false; _settings = null; owner.SetSettingsOpen(false); scope.Dispose();
        }
        try
        {
            window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
            window.Owner = owner; _settings = window;
            services.GetService<ThemeService>()?.TrackDialog(window);
            owner.SetSettingsOpen(true); counted = true;
            window.Closed += (_, _) => Release();
            window.Show();
        }
        catch
        {
            if (counted) { window?.Close(); Release(); }
            else scope.Dispose();
            throw;
        }
    }

    public LaunchItem? EditItem(LauncherWindow owner, LaunchItem? item, bool openIconPicker = false)
    {
        if (!owner.CanEdit || owner.Launcher.DragActive || HasOpenEditor) return null;
        using var scope = services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<EditItemViewModel>();
        model.Initialize(item, owner.CanEdit);
        var window = scope.ServiceProvider.GetRequiredService<EditItemDialog>();
        window.OpenPickerOnLoad = openIconPicker;
        return ShowEditor(owner, window) ? model.Result : null;
    }
    public string? Name(LauncherWindow owner, string title, string value)
    {
        if (!owner.CanEdit || owner.Launcher.DragActive || HasOpenEditor) return null;
        using var scope = services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<NameDialogViewModel>();
        model.Initialize(title, value, owner.CanEdit);
        return ShowEditor(owner, scope.ServiceProvider.GetRequiredService<NameDialog>()) ? model.Value : null;
    }
    public bool ShowEditor(LauncherWindow owner, Window editor)
    {
        if (!owner.CanEdit || owner.Launcher.DragActive || HasOpenEditor) return false;
        editor.Owner = owner; _editor = editor; owner.SetSettingsOpen(true);
        services.GetService<ThemeService>()?.TrackDialog(editor);
        try { return editor.ShowDialog() == true; }
        finally { _editor = null; owner.SetSettingsOpen(false); }
    }
    public async Task<bool> ConfirmDeleteGroupAsync(LauncherWindow owner, string name)
    {
        if (!owner.CanEdit || owner.Launcher.DragActive || HasOpenEditor) return false;
        using var scope = services.CreateScope();
        var dialog = scope.ServiceProvider.GetRequiredService<Wpf.Ui.Controls.MessageBox>();
        dialog.Owner = owner; dialog.Title = "删除分组";
        dialog.Content = $"删除“{name}”及其中的条目？此操作不能用单条撤销恢复。";
        dialog.PrimaryButtonText = "删除"; dialog.PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger;
        dialog.CloseButtonText = "取消"; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.ResizeMode = ResizeMode.NoResize; dialog.ShowInTaskbar = false; dialog.MaxWidth = 460; dialog.FontFamily = owner.FontFamily;
        AutomationProperties.SetName(dialog, dialog.Title);
        services.GetService<ThemeService>()?.TrackDialog(dialog);
        _editor = dialog; owner.SetSettingsOpen(true);
        try { return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary; }
        finally { _editor = null; owner.SetSettingsOpen(false); }
    }
}
