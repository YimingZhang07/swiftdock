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
            if (!LaunchService.IsWebUrl(path)) throw new ArgumentException("请输入有效的 http/https 网址。");
            return kind;
        }
        if (!Path.IsPathFullyQualified(path) || path.Contains('"')) throw new ArgumentException("请输入不含引号的绝对路径。");
        switch (kind)
        {
            case LaunchKind.App:
                if (directoryExists || !IsApplicationPath(path)) throw new ArgumentException("请选择 .exe、.lnk、.bat 或 .cmd 程序。");
                if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && workingDirectory.Length > 0
                    && (!Path.IsPathFullyQualified(workingDirectory) || workingDirectory.Contains('"')))
                    throw new ArgumentException("工作目录必须是有效绝对路径。");
                break;
            case LaunchKind.File:
                if (directoryExists) throw new ArgumentException("请选择文件，不能选择文件夹。");
                break;
            case LaunchKind.Folder:
                if (!directoryExists) throw new ArgumentException("找不到该文件夹。");
                break;
            default: throw new ArgumentException("请选择项目类型。");
        }
        return kind;
    }
}
