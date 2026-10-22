using System.IO;
using Swiftdock.Models;

namespace Swiftdock.Services;

// Callers supply expanded paths and filesystem facts; these rules never inspect the machine.
public static class ItemEditorValidation
{
    public static string NormalizeTarget(string text) => text.Trim().Trim('"');

    public static bool IsApplicationPath(string path) => new[] { ".exe", ".lnk", ".bat", ".cmd" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string? InferUrlName(string text)
    {
        string path = NormalizeTarget(text);
        return LaunchService.IsWebUrl(path) ? new Uri(path).IdnHost : null;
    }

    public static LaunchKind? InferKind(string path, bool fileExists, bool directoryExists)
    {
        if (LaunchService.IsWebUrl(path)) return LaunchKind.Url;
        if (directoryExists) return LaunchKind.Folder;
        if (fileExists) return IsApplicationPath(path) ? LaunchKind.App : LaunchKind.File;
        return null;
    }

    public static LaunchKind Validate(LaunchKind kind, string path, bool directoryExists, string workingDirectory = "")
    {
        if (kind == LaunchKind.Url)
        {
            if (!LaunchService.IsWebUrl(path)) throw L.Exception<ArgumentException>(L.Text("EnterAValidHttpHttpsUrl"));
            return kind;
        }
        if (!Path.IsPathFullyQualified(path) || path.Contains('"')) throw L.Exception<ArgumentException>(L.Text("EnterAnAbsolutePathWithoutQuotes"));
        switch (kind)
        {
            case LaunchKind.App:
                if (directoryExists || !IsApplicationPath(path)) throw L.Exception<ArgumentException>(L.Text("ChooseAnExeLnkBatOrCmdApplication"));
                if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && workingDirectory.Length > 0
                    && (!Path.IsPathFullyQualified(workingDirectory) || workingDirectory.Contains('"')))
                    throw L.Exception<ArgumentException>(L.Text("TheWorkingFolderMustBeAValidAbsolutePath"));
                break;
            case LaunchKind.File:
                if (directoryExists) throw L.Exception<ArgumentException>(L.Text("ChooseAFileInsteadOfAFolder"));
                break;
            case LaunchKind.Folder:
                if (!directoryExists) throw L.Exception<ArgumentException>(L.Text("TheFolderCouldNotBeFound"));
                break;
            default: throw L.Exception<ArgumentException>(L.Text("ChooseAnItemType"));
        }
        return kind;
    }
}
