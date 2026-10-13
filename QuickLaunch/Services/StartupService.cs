using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace QuickLaunch.Services;

public sealed class StartupService
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _registryPath;
    private readonly string _executable;
    public string ValueName { get; }
    public string Command { get; }
    public StartupService(string executable, string configDirectory, string registryPath = RunKey)
    {
        _registryPath = registryPath;
        _executable = executable;
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
        ValueName = "QuickLaunch_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..16];
        Command = $"{Quote(Path.GetFullPath(executable))} --silent --config-dir {Quote(directory)}";
    }
    public bool IsEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(_registryPath); return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase); }
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled && _registryPath == RunKey && !CanStartAtLogin())
            throw new InvalidOperationException("当前使用仓库内的开发运行时。请从独立发布版启用自启，或先安装 .NET 10 Windows Desktop Runtime。");
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath) ?? throw new IOException("无法访问当前用户自启设置。");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
    private bool CanStartAtLogin()
    {
        string directory = Path.GetDirectoryName(_executable)!;
        if (File.Exists(Path.Combine(directory, "coreclr.dll")))
        {
            string config = Path.Combine(directory, "QuickLaunch.runtimeconfig.json");
            if (File.Exists(config))
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
                if (json.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out _)) return true;
            }
        }
        string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        return Directory.Exists(shared) && Directory.EnumerateDirectories(shared, "10.*").Any();
    }
    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
