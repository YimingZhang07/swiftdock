using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Swiftdock.Services;

public sealed record BrowserProgram(string Name, string Path);
public sealed record BrowserRegistration(bool CurrentUser, string? Name, string? LocalizedString, string? Command);

public interface IBrowserRegistry
{
    IEnumerable<BrowserRegistration> Read();
}

public interface IBrowserCatalog
{
    IReadOnlyList<BrowserProgram> Detect();
}

public sealed class BrowserCatalog(IBrowserRegistry registry) : IBrowserCatalog
{
    public IReadOnlyList<BrowserProgram> Detect() => Parse(registry.Read(), File.Exists, ResolveIndirectString,
        Environment.ExpandEnvironmentVariables);

    // All OS access is supplied by the caller, including environment and resource resolution.
    public static IReadOnlyList<BrowserProgram> Parse(IEnumerable<BrowserRegistration> registrations,
        Func<string, bool> exists, Func<string, string?> resolveIndirect, Func<string, string> expand)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var browsers = new List<BrowserProgram>();
        string? Name(string? value) => string.IsNullOrWhiteSpace(value) ? null
            : value.Trim().StartsWith('@') ? resolveIndirect(expand(value.Trim())) : value.Trim();
        foreach (var entry in registrations.OrderByDescending(r => r.CurrentUser))
        {
            string? path = ParseCommand(entry.Command, expand);
            if (path is null || !exists(path) || !paths.Add(path)) continue;
            string name = Name(entry.LocalizedString) ?? Name(entry.Name) ?? System.IO.Path.GetFileNameWithoutExtension(path);
            browsers.Add(new(name, path));
        }
        return browsers.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string? ParseCommand(string? command, Func<string, string> expand)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string text = expand(command.Trim());
        string path;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end < 0) return null;
            path = text[1..end];
        }
        else
        {
            // Unquoted registrations can contain spaces in the executable's path.
            var match = Regex.Match(text, @"^(.+?\.exe)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            path = match.Groups[1].Value;
        }
        return FullExePath(path);
    }

    private static string? FullExePath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string? full = FullExePath(Environment.ExpandEnvironmentVariables(path.Trim()));
        return full is null ? null : ItemIdentity.PortablePath(full);
    }

    public static string ProgramName(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { } description
                && !string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string? ResolveIndirectString(string value)
    {
        var buffer = new StringBuilder(1024);
        return SHLoadIndirectString(value, buffer, (uint)buffer.Capacity, 0) == 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint capacity, nint reserved);
}

internal sealed class WindowsBrowserRegistry : IBrowserRegistry
{
    public IEnumerable<BrowserRegistration> Read()
    {
        var entries = new List<BrowserRegistration>();
        var views = Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : [RegistryView.Registry32];
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in views)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                if (clients is null) continue;
                foreach (string name in clients.GetSubKeyNames())
                {
                    try
                    {
                        using var client = clients.OpenSubKey(name);
                        using var command = client?.OpenSubKey(@"shell\open\command");
                        entries.Add(new(hive == RegistryHive.CurrentUser, client?.GetValue("") as string,
                            client?.GetValue("LocalizedString") as string, command?.GetValue("") as string));
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return entries;
    }
}
