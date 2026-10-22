using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Swiftdock.Services;

public sealed class StartupService
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _registryPath;
    private readonly Func<bool> _canStartAtLogin;
    private readonly Func<string, string?> _readValue;
    private readonly Action<string, string?> _writeValue;
    public string ValueName { get; }
    public string Command { get; }
    public StartupService(string executable, string configDirectory, string registryPath = RunKey, bool portable = false)
        : this(executable, configDirectory, registryPath, name =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return key?.GetValue(name) as string;
        }, (name, command) =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath) ?? throw L.Exception<IOException>(L.Text("CouldNotAccessStartupSettingsForTheCurrentUser"));
            if (command is not null) key.SetValue(name, command, RegistryValueKind.String);
            else key.DeleteValue(name, throwOnMissingValue: false);
        }, portable) { }

    internal StartupService(string executable, string configDirectory, string registryPath,
        Func<string, string?> readValue, Action<string, string?> writeValue, bool portable = false,
        Func<bool>? canStartAtLogin = null)
    {
        _readValue = readValue; _writeValue = writeValue;
        _registryPath = registryPath;
        _canStartAtLogin = canStartAtLogin ?? (() => CanStartAtLogin(
            typeof(StartupService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Any(attribute => attribute.Key == "SelfContained" && string.Equals(attribute.Value, "true", StringComparison.OrdinalIgnoreCase)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App")));
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
        ValueName = "Swiftdock_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..16];
        // Let the portable copy rediscover Data so its cache and logs stay local at login.
        Command = $"{Quote(Path.GetFullPath(executable))} --silent" + (portable ? "" : $" --config-dir {Quote(directory)}");
    }
    public bool IsEnabled => string.Equals(_readValue(ValueName), Command, StringComparison.OrdinalIgnoreCase);
    public void SetEnabled(bool enabled)
    {
        if (enabled && _registryPath == RunKey && !_canStartAtLogin())
            throw L.Exception<InvalidOperationException>(L.Text("TheAppIsUsingTheRepositoryDevelopmentRuntimeEnableStartupFromASta"));
        _writeValue(ValueName, enabled ? Command : null);
    }
    internal static bool CanStartAtLogin(bool selfContained, string sharedDesktopRuntimes)
    {
        // Build metadata survives bundling; do not infer deployment from loose runtime files
        // or DOTNET_ROOT, which Run.ps1 points at the repository's development runtime.
        return selfContained || (Directory.Exists(sharedDesktopRuntimes) &&
            Directory.EnumerateDirectories(sharedDesktopRuntimes, "10.*").Any());
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
