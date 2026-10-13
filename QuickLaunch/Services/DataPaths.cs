using System.IO;

namespace QuickLaunch.Services;

public sealed record DataPaths(string ConfigDirectory, string LogDirectory, bool Portable)
{
    public static string DefaultConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickLaunch");
    public static DataPaths Resolve(string? explicitConfig, string? explicitLogs)
    {
        bool portable = explicitConfig is null && File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
        string directory = Path.GetFullPath(explicitConfig ?? (portable ? AppContext.BaseDirectory : DefaultConfigDirectory));
        string logs = Path.GetFullPath(explicitLogs ?? (portable ? Path.Combine(directory, "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickLaunch", "logs")));
        return new(directory, logs, portable);
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
