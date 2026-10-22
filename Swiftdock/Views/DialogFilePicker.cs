using System.Windows;
using Microsoft.Win32;
using Swiftdock.Models;
using Swiftdock.Services;

namespace Swiftdock.Views;

public sealed class DialogFilePicker : IFilePicker
{
    internal Window? Owner { get; set; }
    public string? PickTarget(LaunchKind kind)
    {
        if (kind == LaunchKind.Folder)
        {
            var dialog = new OpenFolderDialog { Title = "选择文件夹" };
            return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
        }
        var file = new OpenFileDialog { Title = kind == LaunchKind.App ? "选择程序" : "选择文件", CheckFileExists = true,
            Filter = kind == LaunchKind.App ? "程序 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd" : "所有文件 (*.*)|*.*" };
        return file.ShowDialog(Owner) == true ? file.FileName : null;
    }
    public string? PickIcon()
    {
        var dialog = new OpenFileDialog { Title = "选择图标", CheckFileExists = true,
            Filter = "图标和图片 (*.ico;*.png;*.jpg;*.bmp;*.exe;*.dll)|*.ico;*.png;*.jpg;*.bmp;*.exe;*.dll" };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
