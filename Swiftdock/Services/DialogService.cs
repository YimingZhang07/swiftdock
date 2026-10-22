using System.Windows;
using System.Windows.Automation;
using Microsoft.Extensions.DependencyInjection;
using Swiftdock.Models;
using Swiftdock.ViewModels;
using Swiftdock.Views;

namespace Swiftdock.Services;

public interface IDialogService
{
    bool HasOpenEditor { get; }
    bool ActivateEditor();
    bool ActivateSettings();
    void ShowSettings(LauncherWindow owner);
    void CloseSettings();
    void CloseEditor();
    LaunchItem? EditItem(LauncherWindow owner, LaunchItem? item, bool openIconPicker = false);
    string? Name(LauncherWindow owner, UiText title, string value);
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
    public string? Name(LauncherWindow owner, UiText title, string value)
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
        L.LocalizeChrome(dialog);
        dialog.Owner = owner; L.Bind(dialog, Window.TitleProperty, L.Text("DeleteGroup"));
        L.Bind(dialog, System.Windows.Controls.ContentControl.ContentProperty, L.Text("Delete0AndItsItemsThisCannotBeUndoneWithItemUndo", name));
        L.Bind(dialog, Wpf.Ui.Controls.MessageBox.PrimaryButtonTextProperty, L.Text("Delete")); dialog.PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger;
        L.Bind(dialog, Wpf.Ui.Controls.MessageBox.CloseButtonTextProperty, L.Text("Cancel")); dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.ResizeMode = ResizeMode.NoResize; dialog.ShowInTaskbar = false; dialog.MaxWidth = 460; dialog.FontFamily = owner.FontFamily;
        L.Bind(dialog, AutomationProperties.NameProperty, L.Text("DeleteGroup"));
        services.GetService<ThemeService>()?.TrackDialog(dialog);
        _editor = dialog; owner.SetSettingsOpen(true);
        try { return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary; }
        finally { _editor = null; owner.SetSettingsOpen(false); }
    }
}
