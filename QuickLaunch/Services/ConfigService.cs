using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using QuickLaunch.Models;

namespace QuickLaunch.Services;

public interface IConfigWriter { void Write(string path, byte[] bytes); }

public sealed class AtomicConfigWriter : IConfigWriter
{
    public void Write(string path, byte[] bytes)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $"config.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class ConfigService : IAsyncDisposable
{
    private sealed record WriteRequest(AppConfig Snapshot, long Revision, TaskCompletionSource Completion);
    private readonly Channel<WriteRequest> _queue = Channel.CreateUnbounded<WriteRequest>(new() { SingleReader = true });
    private readonly IConfigWriter _writer;
    private FileStream? _lease;
    private readonly Task _worker;
    private long _savedRevision;
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "config.json");
    public AppConfig Initial { get; private set; } = AppConfig.CreateDefault();
    public bool IsReadOnly { get; private set; }
    public string? Notice { get; private set; }
    public long SavedRevision => Interlocked.Read(ref _savedRevision);

    private ConfigService(string directory, bool readOnly, IConfigWriter writer)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _writer = writer;
        IsReadOnly = readOnly;
        if (!readOnly)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                _lease = new(Path.Combine(DirectoryPath, "config.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MakeReadOnly($"配置目录被其他会话占用或不可写，当前只读：{ex.Message}"); }
        }
        Load();
        _worker = Task.Run(WriteLoopAsync);
    }

    public static ConfigService Open(string directory, bool readOnly = false, IConfigWriter? writer = null)
        => new(directory, readOnly, writer ?? new AtomicConfigWriter());

    private void MakeReadOnly(string message)
    {
        IsReadOnly = true;
        Notice = message;
        _lease?.Dispose();
        _lease = null;
    }

    private static AppConfig Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置超过 4MB 限制。");
        using var memory = new MemoryStream();
        file.CopyTo(memory);
        return ConfigValidation.Parse(memory.ToArray());
    }

    private void Load()
    {
        bool primaryBad = false, backupBad = false;
        string? primaryError = null;
        try { Initial = Read(FilePath); return; }
        catch (FutureConfigException ex) { MakeReadOnly(ex.Message); return; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { primaryBad = true; primaryError = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MakeReadOnly($"读取配置失败，未覆盖原文件：{ex.Message}"); return; }

        AppConfig? recovered = null;
        try { recovered = Read(FilePath + ".bak"); }
        catch (FutureConfigException ex) { MakeReadOnly(ex.Message); return; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { backupBad = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MakeReadOnly($"读取备份失败，未覆盖原文件：{ex.Message}"); return; }

        Initial = recovered ?? AppConfig.CreateDefault();
        if (IsReadOnly)
        {
            Notice = $"{Notice ?? "配置只读。"} {(recovered is null ? "使用临时默认内容。" : "从备份只读加载。")}";
            return;
        }
        try
        {
            // Remove corrupt files from replacement destinations without overwriting the good backup.
            if (primaryBad) Archive(FilePath, "config");
            if (backupBad) Archive(FilePath + ".bak", "backup");
            _savedRevision = -1; // Initial snapshot must be written by the same serial worker.
            Notice = recovered is not null ? "已从备份恢复配置，损坏文件已保留。"
                : primaryBad || backupBad ? $"原配置损坏并已保留，当前使用默认内容。{primaryError}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MakeReadOnly($"无法保留损坏文件，已转为只读且停止自动修复：{ex.Message}"); }
    }

    private void Archive(string path, string prefix) => File.Move(path,
        Path.Combine(DirectoryPath, $"{prefix}.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));

    // Caller owns snapshot creation on the UI thread; no live bound collections reach this queue.
    public Task SaveAsync(AppConfig snapshot, long revision)
    {
        if (IsReadOnly) return Task.FromException(new InvalidOperationException("配置只读，禁止写入。"));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new(snapshot, revision, completion)))
            completion.TrySetException(new ObjectDisposedException(nameof(ConfigService)));
        return completion.Task;
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var first in _queue.Reader.ReadAllAsync())
        {
            var batch = new List<WriteRequest> { first };
            while (_queue.Reader.TryRead(out var next)) batch.Add(next);
            var newest = batch.MaxBy(r => r.Revision)!;
            try
            {
                if (newest.Revision > SavedRevision)
                {
                    var validated = ConfigValidation.Normalize(newest.Snapshot);
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(validated, ConfigJsonContext.Default.AppConfig);
                    _writer.Write(FilePath, bytes);
                    Interlocked.Exchange(ref _savedRevision, newest.Revision);
                }
                foreach (var request in batch) request.Completion.TrySetResult();
            }
            catch (Exception ex)
            { foreach (var request in batch) request.Completion.TrySetException(ex); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        _lease?.Dispose();
        _lease = null;
    }
}
