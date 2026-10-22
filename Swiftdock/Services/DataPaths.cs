using System.IO;

namespace Swiftdock.Services;

public sealed record DataPaths(string ConfigDirectory, string LogDirectory, bool Portable)
{
    internal bool UsesDefaultProfile { get; init; }
    internal static string LegacyConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickLaunch");
    public string IconDirectory => Portable ? Path.Combine(ConfigDirectory, "icons") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swiftdock", "icons",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ConfigDirectory.ToUpperInvariant())))[..24]);
    public static string DefaultConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swiftdock");
    public static DataPaths Resolve(string? explicitConfig, string? explicitLogs)
        => Resolve(explicitConfig, explicitLogs, AppContext.BaseDirectory);

    internal static DataPaths Resolve(string? explicitConfig, string? explicitLogs, string baseDirectory)
    {
        string dataDirectory = Path.Combine(baseDirectory, "Data");
        bool portable = explicitConfig is null && Directory.Exists(dataDirectory);
        string directory = Path.GetFullPath(explicitConfig ?? (portable ? dataDirectory : DefaultConfigDirectory));
        string logs = Path.GetFullPath(explicitLogs ?? (portable ? Path.Combine(directory, "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swiftdock", "logs")));
        return new(directory, logs, portable) { UsesDefaultProfile = explicitConfig is null && !portable };
    }

    internal bool MigrateLegacyConfig(string? legacyDirectory = null)
    {
        if (!UsesDefaultProfile) return false;
        string destination = Path.Combine(ConfigDirectory, "config.json");
        string source = Path.Combine(legacyDirectory ?? LegacyConfigDirectory, "config.json");
        if (File.Exists(destination) || !File.Exists(source)) return false;
        Directory.CreateDirectory(ConfigDirectory);
        // Copy the backup first so a failed primary copy can be retried next startup.
        // Never replace either existing file, and leave the legacy profile intact.
        if (File.Exists(source + ".bak") && !File.Exists(destination + ".bak"))
            File.Copy(source + ".bak", destination + ".bak");
        File.Copy(source, destination);
        return true;
    }

    public static bool CanWrite(string directory)
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(directory);
            probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
