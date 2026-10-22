using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Swiftdock.Services;

public sealed class StartupService
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _registryPath;
    private readonly Func<bool> _canStartAtLogin;
    private readonly Func<string, string?> _readValue;
    private readonly Action<string, string?> _writeValue;
    private readonly Func<string[]> _valueNames;
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
        }, portable, valueNames: () =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return key?.GetValueNames() ?? [];
        }) { }

    internal StartupService(string executable, string configDirectory, string registryPath,
        Func<string, string?> readValue, Action<string, string?> writeValue, bool portable = false,
        Func<bool>? canStartAtLogin = null, Func<string[]>? valueNames = null)
    {
        _readValue = readValue; _writeValue = writeValue;
        _valueNames = valueNames ?? (() => []);
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
    internal int MigrateLegacyDefaultProfile(string? legacyDirectory = null)
    {
        string legacy = legacyDirectory ?? DataPaths.LegacyConfigDirectory;
        string[] matches = _valueNames().Where(name => name.StartsWith("QuickLaunch_", StringComparison.OrdinalIgnoreCase)
            && TargetsLegacyDefaultProfile(_readValue(name), legacy)).ToArray();
        if (matches.Length == 0) return 0; // A Run value is the old user's opt-in.
        SetEnabled(true); // Write successfully before removing any old value.
        foreach (string name in matches) _writeValue(name, null);
        return matches.Length;
    }

    private static bool TargetsLegacyDefaultProfile(string? command, string legacyDirectory)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        nint arguments = CommandLineToArgvW(command, out int count);
        if (arguments == 0) return false;
        try
        {
            string[] args = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * nint.Size))!).ToArray();
            if (args.Length == 0 || !Path.IsPathFullyQualified(args[0])) return false;
            int index = Array.IndexOf(args, "--config-dir");
            if (index >= 0)
            {
                // Ambiguous or malformed commands are not ours to alter.
                if (index + 1 >= args.Length || args.Count(arg => arg == "--config-dir") != 1) return false;
                string directory = Environment.ExpandEnvironmentVariables(args[index + 1]);
                return Path.IsPathFullyQualified(directory) && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyDirectory)), StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(Path.GetFileName(args[0]), "QuickLaunch.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        finally { LocalFree(arguments); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
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
