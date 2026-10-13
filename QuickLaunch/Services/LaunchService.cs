using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using QuickLaunch.Models;

namespace QuickLaunch.Services;

public sealed record LaunchResult(bool Accepted, bool Cancelled, string Message);

public static class LaunchService
{
    public static ProcessStartInfo CreateStartInfo(LaunchItem item, bool elevate = false)
    {
        string path = item.Kind == LaunchKind.Url ? item.Path : Environment.ExpandEnvironmentVariables(item.Path);
        if (item.Kind == LaunchKind.Url && !IsWebUrl(path)) throw new ArgumentException("只支持 http/https 网址。");
        var info = new ProcessStartInfo(path) { UseShellExecute = true };
        if (item.Kind == LaunchKind.App)
        {
            if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                info.Arguments = item.Arguments;
                info.WorkingDirectory = Environment.ExpandEnvironmentVariables(item.WorkingDirectory);
                if (info.WorkingDirectory.Length == 0 && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    info.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
            }
            if (elevate || item.RunAsAdmin) info.Verb = "runas";
        }
        return info;
    }

    public static bool IsWebUrl(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0;

    public static Task<LaunchResult> StartAsync(LaunchItem snapshot, bool elevate = false) => Task.Run(() =>
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(snapshot, elevate));
            return new LaunchResult(true, false, "Shell 已接受启动请求");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LaunchResult(false, true, "已取消提权");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return new LaunchResult(false, false, ex.Message);
        }
    });
}
