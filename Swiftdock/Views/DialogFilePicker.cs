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
            var dialog = new OpenFolderDialog { Title = L.Text("ChooseFolder") };
            return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
        }
        var file = new OpenFileDialog { Title = kind == LaunchKind.App ? L.Text("ChooseApplication") : L.Text("ChooseFile"), CheckFileExists = true,
            Filter = kind == LaunchKind.App ? L.Text("ApplicationsExeLnkBatCmdExeLnkBatCmd") : L.Text("AllFiles") };
        return file.ShowDialog(Owner) == true ? file.FileName : null;
    }
    public string? PickIcon()
    {
        var dialog = new OpenFileDialog { Title = L.Text("ChooseIcon"), CheckFileExists = true,
            Filter = L.Text("IconsAndImagesIcoPngJpgBmpExeDllIcoPngJpgBmpExeDll") };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
