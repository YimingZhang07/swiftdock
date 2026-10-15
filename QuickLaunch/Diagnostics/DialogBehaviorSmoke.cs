using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.Views;
using Ui = Wpf.Ui.Controls;

namespace QuickLaunch.Diagnostics;

internal static class DialogBehaviorSmoke
{
    internal static async Task RunAsync(LauncherWindow main, string directory, string profileDirectory, Action<string, bool> check)
    {
        CheckSettings(main, profileDirectory, check);
        CheckNames(main, check);
        await CheckDeleteAsync(main, directory, check);
    }

    private static void CheckSettings(LauncherWindow main, string directory, Action<string, bool> check)
    {
        var values = new Dictionary<string, string>();
        int writes = 0;
        var startup = new StartupService(Environment.ProcessPath!, directory, @"Software\QuickLaunch\Tests", values.GetValueOrDefault, (name, command) =>
        {
            writes++;
            if (command is null) values.Remove(name); else values[name] = command;
        });
        bool reset = false;
        HotkeyConfig? applied = null;
        var model = main.ViewModel;
        bool originalIcons = model.AutoFetchWebsiteIcons;
        string originalPlacement = model.Placement;
        var settings = new SettingsWindow(model, directory, "当前已注册：Alt+Space", candidate =>
            { applied = candidate; return ("已应用", false); }, startup, () => reset = true, () => { }) { Owner = main };
        try
        {
            settings.Show(); WindowSmoke.Pump();
            DialogAppearanceSmoke.CheckTitleBar(settings, "behavior", check);
            check("settings-card-rows-and-symbols", LauncherWindow.Descendants<Ui.CardControl>(settings).Count() == 6
                && LauncherWindow.Descendants<Ui.CardControl>(settings).All(card => card.Icon is Ui.SymbolIcon));
            check("settings-toggle-controls", settings.StartupBox is Ui.ToggleSwitch && settings.WebsiteIconsBox is Ui.ToggleSwitch);
            ClickToggle(settings.WebsiteIconsBox, false);
            check("settings-website-toggle-live-off", !model.AutoFetchWebsiteIcons);
            ClickToggle(settings.WebsiteIconsBox, true);
            check("settings-website-toggle-live-on", model.AutoFetchWebsiteIcons);
            model.SetAutoFetchWebsiteIcons(false);
            check("settings-website-toggle-follows-model", settings.WebsiteIconsBox.IsChecked == false);
            ClickToggle(settings.StartupBox, true);
            check("settings-startup-toggle-registers", startup.IsEnabled && values[startup.ValueName] == startup.Command && writes == 1 && settings.Result.Text == "自启设置已更新。");
            ClickToggle(settings.StartupBox, false);
            check("settings-startup-toggle-unregisters", !startup.IsEnabled && values.Count == 0 && writes == 2);
            settings.FollowMouse.IsChecked = true;
            check("settings-placement-live-applies", model.Placement == "cursor");
            model.SetPlacement("lastPosition");
            check("settings-placement-follows-model", settings.LastPosition.IsChecked == true);
            settings.Alt.IsChecked = false; settings.Control.IsChecked = true;
            settings.Shift.IsChecked = true; settings.Windows.IsChecked = false; settings.MainKey.SelectedItem = "F8";
            settings.ApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check("settings-hotkey-applies-selected-combination", applied is { Key: "F8" } && applied.Modifiers.SequenceEqual(["Control", "Shift"]));
            settings.ResetSizeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check("settings-reset-calls-supplied-path", reset);
        }
        finally
        {
            settings.Close(); WindowSmoke.Pump();
            model.SetAutoFetchWebsiteIcons(originalIcons); model.SetPlacement(originalPlacement);
        }

        var invalidStartup = new StartupService(Environment.ProcessPath!, directory, @"Software\QuickLaunch\Tests",
            _ => throw new IOException("读取自启失败"), (_, _) => throw new IOException("更新自启失败"));
        var failed = new SettingsWindow(model, directory, "", _ => ("", false), invalidStartup, () => { }, () => { }) { Owner = main };
        try
        {
            failed.Show(); WindowSmoke.Pump();
            check("settings-startup-read-error-disables-toggle", !failed.StartupBox.IsEnabled && failed.Result.Text == "读取自启失败"
                && failed.Result.Foreground == failed.FindResource("SystemFillColorCriticalBrush"));
            failed.StartupBox.IsEnabled = true;
            ClickToggle(failed.StartupBox, true);
            check("settings-startup-write-error-restores-off", failed.StartupBox.IsChecked == false && failed.Result.Text == "更新自启失败"
                && failed.Result.Foreground == failed.FindResource("SystemFillColorCriticalBrush"));
        }
        finally { failed.Close(); WindowSmoke.Pump(); }
    }

    private static void ClickToggle(Ui.ToggleSwitch toggle, bool value)
    {
        toggle.IsChecked = value;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        WindowSmoke.Pump();
    }

    private static void CheckNames(LauncherWindow main, Action<string, bool> check)
    {
        int initial = OwnedCount(main);
        foreach (bool accept in new[] { true, false })
        {
            var dialog = new NameDialog("新建分组", "") { Owner = main };
            bool timedOut = false;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (_, _) => { timedOut = true; dialog.Close(); };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                WindowSmoke.Pump();
                DialogAppearanceSmoke.CheckTitleBar(dialog, "modal", check);
                check("name-dialog-named-elements", new[] { "NameBox", "Error" }.All(n => dialog.FindName(n) is FrameworkElement));
                check("name-dialog-placeholder", dialog.NameBox.PlaceholderText == "名称");
                check("name-dialog-owned-while-modal", main.HasOpenEditor && OwnedCount(main) == initial + 1);
                main.Toggle();
                check("name-dialog-toggle-keeps-owner-visible", main.IsVisible && dialog.IsVisible);
                var buttons = LauncherWindow.Descendants<Button>(dialog).ToArray();
                var primary = buttons.Single(b => b.IsDefault);
                check("name-dialog-primary-and-cancel", primary is Ui.Button { Appearance: Ui.ControlAppearance.Primary }
                    && buttons.Count(b => b.IsCancel) == 1);
                primary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check("name-dialog-empty-validation", dialog.IsVisible && dialog.Error.Text == "请输入名称。"
                    && dialog.Error.Foreground == dialog.FindResource("SystemFillColorCriticalBrush"));
                dialog.NameBox.Text = "  新名称  ";
                if (accept) primary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                else ((IInvokeProvider)new ButtonAutomationPeer(buttons.Single(b => b.IsCancel)).GetPattern(PatternInterface.Invoke)).Invoke();
            });
            timeout.Start();
            try
            {
                bool result = main.ShowEditor(dialog);
                WindowSmoke.Pump();
                check($"name-dialog-result-{accept}", !timedOut && result == accept && dialog.Value == "新名称");
                check($"name-dialog-releases-ownership-{accept}", !main.HasOpenEditor && OwnedCount(main) == initial);
            }
            finally { timeout.Stop(); dialog.Close(); WindowSmoke.Pump(); }
        }
    }

    private static async Task CheckDeleteAsync(LauncherWindow main, string directory, Action<string, bool> check)
    {
        using var theme = new ThemeSmokeScope();
        var original = main.SelectedGroup;
        int initial = OwnedCount(main);
        foreach (bool dark in new[] { true, false })
        foreach (bool confirm in new[] { false, true })
        {
            ThemeSmokeScope.Apply(dark);
            var group = main.ViewModel.AddGroup("待删除分组");
            group.Items.Add(new("示例网站", "https://example.com/", LaunchKind.Url));
            bool inspected = false;
            var pending = main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var dialog = main.OwnedWindows.OfType<Ui.MessageBox>().Single();
                try
                {
                    WindowSmoke.Pump(); dialog.UpdateLayout(); inspected = true;
                    check($"delete-dialog-owned-{dark}-{confirm}", dialog.Owner == main && main.HasOpenEditor && OwnedCount(main) == initial + 1
                        && !dialog.ShowInTaskbar && dialog.ResizeMode == ResizeMode.NoResize && dialog.WindowStartupLocation == WindowStartupLocation.CenterOwner);
                    var buttons = LauncherWindow.Descendants<Ui.Button>(dialog).ToArray();
                    check($"delete-dialog-critical-primary-and-cancel-{dark}-{confirm}", buttons.Any(b => b.IsDefault && b.Content as string == "删除" && b.Appearance == Ui.ControlAppearance.Danger)
                        && buttons.Any(b => b.IsCancel && b.Content as string == "取消") && string.IsNullOrEmpty(dialog.SecondaryButtonText));
                    main.Toggle();
                    check($"delete-dialog-toggle-keeps-owner-visible-{dark}-{confirm}", main.IsVisible && dialog.IsVisible);
                    if (!confirm) PrototypeSmoke.SaveSnapshot(dialog, Path.Combine(directory, dark ? "delete-group.png" : "delete-group-light.png"));
                }
                finally { dialog.TemplateButtonCommand.Execute(confirm ? Ui.MessageBoxButton.Primary : Ui.MessageBoxButton.Close); }
            });
            try
            {
                await main.DeleteGroupAsync(group);
                WindowSmoke.Pump();
                check($"delete-group-result-{dark}-{confirm}", inspected && main.Groups.Contains(group) != confirm);
                check($"delete-group-releases-ownership-{dark}-{confirm}", !main.HasOpenEditor && OwnedCount(main) == initial);
            }
            finally
            {
                pending.Abort();
                if (main.Groups.Contains(group)) main.ViewModel.RemoveGroup(group);
                main.SelectGroup(original); WindowSmoke.Pump();
            }
        }
        var empty = main.ViewModel.AddGroup("空分组");
        await main.DeleteGroupAsync(empty); WindowSmoke.Pump();
        check("delete-empty-group-skips-confirmation", !main.Groups.Contains(empty) && !main.HasOpenEditor && OwnedCount(main) == initial);
        main.SelectGroup(original);
    }

    private static int OwnedCount(LauncherWindow window) =>
        (int)typeof(LauncherWindow).GetField("_ownedWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
