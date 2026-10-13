using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace QuickLaunch.Services;

public sealed class DiagnosticLog : IAsyncDisposable
{
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Task _writer;
    public string FilePath { get; }
    public string? Failure { get; private set; }

    public DiagnosticLog(string directory)
    {
        Directory.CreateDirectory(directory);
        // A unique session file avoids collisions between M0 prototypes.
        FilePath = Path.Combine(directory, $"m0-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        _writer = Task.Run(async () =>
        {
            try
            {
                await using var stream = new StreamWriter(FilePath, append: false);
                long written = 0;
                await foreach (var line in _queue.Reader.ReadAllAsync())
                {
                    if (written > 4 * 1024 * 1024) continue; // Session cap; never grow without bound.
                    await stream.WriteLineAsync(line);
                    await stream.FlushAsync();
                    written += line.Length * 2L;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failure = ex.Message;
            }
        });
    }

    public void Write(string name, object? data = null) => _queue.Writer.TryWrite(
        JsonSerializer.Serialize(new { time = DateTimeOffset.Now, name, data }));

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
