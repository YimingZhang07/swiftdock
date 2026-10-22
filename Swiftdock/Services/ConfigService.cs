using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Swiftdock.Models;

namespace Swiftdock.Services;

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
    public UiText? Notice { get; private set; }
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
            { MakeReadOnly(L.Text("TheConfigurationFolderIsInUseOrNotWritableRunningReadOnly0", L.Error(ex))); }
        }
        Load();
        _worker = Task.Run(WriteLoopAsync);
    }

    public static ConfigService Open(string directory, bool readOnly = false, IConfigWriter? writer = null)
        => new(directory, readOnly, writer ?? new AtomicConfigWriter());

    private void MakeReadOnly(UiText message)
    {
        IsReadOnly = true;
        Notice = message;
        _lease?.Dispose();
        _lease = null;
    }

    private static AppConfig Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 4 * 1024 * 1024) throw L.Exception<InvalidDataException>(L.Text("ConfigurationExceedsThe4MbLimit"));
        using var memory = new MemoryStream();
        file.CopyTo(memory);
        return ConfigValidation.Parse(memory.ToArray());
    }

    private void Load()
    {
        bool primaryBad = false, backupBad = false;
        UiText? primaryError = null;
        try { Initial = Read(FilePath); return; }
        catch (FutureConfigException ex) { MakeReadOnly(L.Error(ex)); return; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { primaryBad = true; primaryError = L.Error(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MakeReadOnly(L.Text("CouldNotReadConfigurationTheOriginalFileWasKept0", L.Error(ex))); return; }

        AppConfig? recovered = null;
        try { recovered = Read(FilePath + ".bak"); }
        catch (FutureConfigException ex) { MakeReadOnly(L.Error(ex)); return; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { backupBad = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MakeReadOnly(L.Text("CouldNotReadTheBackupTheOriginalFileWasKept0", L.Error(ex))); return; }

        Initial = recovered ?? AppConfig.CreateDefault();
        if (IsReadOnly)
        {
            Notice = L.Text("ReadOnlyNotice", Notice ?? L.Text("ConfigurationIsReadOnly"), recovered is null ? L.Text("UsingTemporaryDefaultContent") : L.Text("LoadedTheBackupReadOnly"));
            return;
        }
        try
        {
            // Remove corrupt files from replacement destinations without overwriting the good backup.
            if (primaryBad) Archive(FilePath, "config");
            if (backupBad) Archive(FilePath + ".bak", "backup");
            _savedRevision = -1; // Initial snapshot must be written by the same serial worker.
            Notice = recovered is not null ? L.Text("ConfigurationRestoredFromBackupTheDamagedFileWasKept")
                : primaryBad || backupBad ? L.Text("TheDamagedConfigurationWasKeptUsingDefaultContent0", primaryError) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MakeReadOnly(L.Text("CouldNotPreserveTheDamagedFileRunningReadOnlyAutomaticRepairStopp", L.Error(ex))); }
    }

    private void Archive(string path, string prefix) => File.Move(path,
        Path.Combine(DirectoryPath, $"{prefix}.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));

    // Caller owns snapshot creation on the UI thread; no live bound collections reach this queue.
    public Task SaveAsync(AppConfig snapshot, long revision)
    {
        if (IsReadOnly) return Task.FromException(L.Exception<InvalidOperationException>(L.Text("ConfigurationIsReadOnlyWritingIsDisabled")));
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
                    if (bytes.Length > 4 * 1024 * 1024) throw L.Exception<InvalidDataException>(L.Text("ConfigurationExceeds4MbTheExistingFileWasKept"));
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
