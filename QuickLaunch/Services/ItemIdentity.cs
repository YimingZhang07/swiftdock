using System.IO;
using QuickLaunch.Models;

namespace QuickLaunch.Services;

public static class ItemIdentity
{
    public static string FilePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))).ToUpperInvariant();
    public static string Key(LaunchItem item)
    {
        if (item.Kind == LaunchKind.Url)
        {
            var uri = new Uri(item.Path, UriKind.Absolute);
            return $"url|{uri.Scheme.ToLowerInvariant()}://{(uri.UserInfo.Length == 0 ? "" : uri.UserInfo + "@")}"
                + uri.IdnHost.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port)
                + uri.AbsolutePath + uri.Query + uri.Fragment;
        }
        string path = FilePath(item.Path);
        if (item.Kind != LaunchKind.App) return $"{item.Kind}|{path}";
        bool shortcut = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        string working = shortcut ? "" : item.WorkingDirectory;
        if (working.Length == 0 && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) working = Path.GetDirectoryName(path) ?? "";
        return $"app|{path}|{(shortcut ? "" : item.Arguments)}|{(working.Length == 0 ? "" : FilePath(working))}|{item.RunAsAdmin}";
    }

    public static string PortablePath(string path, string? userProfile = null)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        if (full.Equals(profile, StringComparison.OrdinalIgnoreCase)) return "%USERPROFILE%";
        if (full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "%USERPROFILE%" + full[profile.Length..];
        return full;
    }
}
