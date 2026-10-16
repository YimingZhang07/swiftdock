using System.IO;

namespace QuickLaunch.Snapshots;

// A locked progress file must not interrupt rendering or window cleanup.
internal sealed class ManifestWriter(
    Action<string, string>? write = null, Action<string, string>? replace = null, Action<int>? delay = null)
{
    private readonly Action<string, string> _write = write ?? File.WriteAllText;
    private readonly Action<string, string> _replace = replace ?? ((source, target) => File.Move(source, target, overwrite: true));
    private readonly Action<int> _delay = delay ?? Thread.Sleep;

    internal string? TryWrite(string path, string json)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _write(path + ".tmp", json);
                _replace(path + ".tmp", path);
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                last = error;
                if (attempt < 2) _delay(25 * (attempt + 1));
            }
        }
        try { _write(path, json); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return $"Atomic report write: {last!.Message}; direct write: {error.Message}"; }
    }
}
