using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swiftdock.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Xunit;

namespace Swiftdock.Tests;

public sealed class LoggingTests
{
    [Fact]
    public async Task ConcurrentSessionsShareRollingFileAndRemainSelectable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-shared-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var firstDiagnostics = new LoggingDiagnostics();
        var secondDiagnostics = new LoggingDiagnostics();
        var first = SessionLogging.Create(directory, firstDiagnostics);
        var second = SessionLogging.Create(directory, secondDiagnostics);
        try
        {
            using var firstFactory = new SerilogLoggerFactory(first, dispose: false);
            using var secondFactory = new SerilogLoggerFactory(second, dispose: false);
            var firstLogger = firstFactory.CreateLogger<LoggingTests>();
            var secondLogger = secondFactory.CreateLogger<LoggingTests>();
            long WrittenBytes() => Directory.GetFiles(directory, "swiftdock-*.jsonl").Sum(file => new FileInfo(file).Length);
            async Task WaitForWrite(long previous)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (WrittenBytes() <= previous) await Task.Delay(10, timeout.Token);
            }
            firstLogger.HotkeyChanged("first-open");
            await WaitForWrite(0);
            long beforeSecond = WrittenBytes();
            secondLogger.HotkeyChanged("second-open");
            await WaitForWrite(beforeSecond);
            // Both sinks hold the file open before either is disposed.
            Assert.Single(Directory.GetFiles(directory, "swiftdock-*.jsonl"));
            await Task.WhenAll(Task.Run(() =>
            {
                for (int i = 0; i < 32; i++) firstLogger.HotkeyChanged("first");
            }), Task.Run(() =>
            {
                for (int i = 0; i < 32; i++) secondLogger.HotkeyChanged("second");
            }));
            await first.DisposeAsync(); await second.DisposeAsync();
            Assert.Null(firstDiagnostics.Failure); Assert.Null(secondDiagnostics.Failure);
            Assert.NotEqual(firstDiagnostics.Session, secondDiagnostics.Session);
            Assert.True(Guid.TryParse(firstDiagnostics.Session, out _));
            Assert.True(Guid.TryParse(secondDiagnostics.Session, out _));
            var firstEvents = ReadSession(directory, firstDiagnostics.Session);
            var secondEvents = ReadSession(directory, secondDiagnostics.Session);
            Assert.Equal(33, firstEvents.Length); Assert.Equal(33, secondEvents.Length);
            Assert.All(firstEvents, entry => Assert.StartsWith("first", entry.GetProperty("hotkey").GetString()));
            Assert.All(secondEvents, entry => Assert.StartsWith("second", entry.GetProperty("hotkey").GetString()));
        }
        finally
        {
            await first.DisposeAsync(); await second.DisposeAsync();
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static JsonElement[] ReadSession(string directory, string session) => Directory.GetFiles(directory, "swiftdock-*.jsonl")
        .OrderByDescending(File.GetLastWriteTimeUtc).SelectMany(File.ReadLines).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        }).Where(entry => entry.TryGetProperty("Pid", out var pid) && pid.GetInt32() == Environment.ProcessId
            && entry.TryGetProperty("Session", out var id) && id.GetString() == session).ToArray();

    [Fact]
    public async Task FileCreationAndWritesCannotBlockLoggingCaller()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int callerThread = Environment.CurrentManagedThreadId, fileThread = 0;
        var sink = new CountingSink();
        var log = SessionLogging.Create("unused", new LoggingDiagnostics(), _ =>
        {
            fileThread = Environment.CurrentManagedThreadId;
            entered.Set(); release.Wait(TimeSpan.FromSeconds(5));
            return sink;
        });
        try
        {
            using var factory = new SerilogLoggerFactory(log, dispose: false);
            var logger = factory.CreateLogger<LoggingTests>();
            logger.HotkeyChanged("first");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.NotEqual(callerThread, fileThread);
            logger.HotkeyChanged("second-while-file-open-blocked");
            Assert.Equal(0, sink.Count);
        }
        finally { release.Set(); await log.DisposeAsync(); }
        Assert.Equal(2, sink.Count);
    }

    private sealed class CountingSink : ILogEventSink
    {
        internal int Count { get; private set; }
        public void Emit(LogEvent logEvent) => Count++;
    }

    [Fact]
    public async Task FileSinkRollsAtApproximatelyFourMegabytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-log-size-" + Guid.NewGuid().ToString("N"));
        var diagnostics = new LoggingDiagnostics();
        var log = SessionLogging.Create(directory, diagnostics);
        try
        {
            using (var factory = new SerilogLoggerFactory(log, dispose: false))
            {
                var logger = factory.CreateLogger<LoggingTests>();
                string payload = new('x', 128 * 1024);
                for (int i = 0; i < 40; i++) logger.HotkeyChanged(payload);
            }
            await log.DisposeAsync();
            Assert.Null(diagnostics.Failure);
            var files = Directory.GetFiles(directory, "swiftdock-*.jsonl");
            Assert.Equal(2, files.Length);
            Assert.Equal(40, ReadSession(directory, diagnostics.Session).Length);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 4 * 1024 * 1024 + 129 * 1024));
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }
    }

    [Fact]
    public async Task GeneratedMessagesPreserveEventNamesTypedPropertiesAndClef()
    {
        await using var sink = new TestLogging();
        var logger = sink.Logger<LoggingTests>();
        logger.ShowUiProxy("hotkey", 12.5, true, true, false, false);
        logger.LaunchResult(2, false, true, "cancelled");
        logger.FaviconResult("https://example.test", "success", 17);
        logger.DragCommit("item-id", "one", "two");
        logger.HotkeyChanged("check-name");
        logger.Startup("10.0.12", "Windows", false, "v0.1.3", 456.7, "profile", false, null);
        logger.DropCapture(["FileDrop", "Text"], 2, 1.5);
        string[] expected = ["show.ui-proxy", "launch.result", "favicon.result", "drag.commit", "hotkey.changed", "startup", "drop.capture"];
        var events = sink.Events.ToArray();
        Assert.Equal(expected.Length, events.Length);
        for (int i = 0; i < events.Length; i++)
        {
            using var json = JsonDocument.Parse(TestLogging.Clef(events[i]));
            var root = json.RootElement;
            Assert.Equal(expected[i], root.GetProperty("EventId").GetProperty("Name").GetString());
            Assert.Equal(typeof(LoggingTests).FullName, root.GetProperty("SourceContext").GetString());
            Assert.True(root.TryGetProperty("@t", out _)); Assert.True(root.TryGetProperty("@mt", out _));
            Assert.False(root.TryGetProperty("data", out _));
            switch (i)
            {
                case 0:
                    Assert.Equal("hotkey", root.GetProperty("source").GetString());
                    Assert.Equal(12.5, root.GetProperty("milliseconds").GetDouble());
                    Assert.True(root.GetProperty("foreground").GetBoolean());
                    Assert.True(root.GetProperty("focus").GetBoolean());
                    Assert.False(root.GetProperty("forDrop").GetBoolean());
                    Assert.False(root.GetProperty("physicalPresentationMeasured").GetBoolean());
                    break;
                case 1:
                    Assert.Equal(2, root.GetProperty("Kind").GetInt32());
                    Assert.False(root.GetProperty("Accepted").GetBoolean());
                    Assert.True(root.GetProperty("Cancelled").GetBoolean());
                    Assert.Equal("cancelled", root.GetProperty("Message").GetString());
                    break;
                case 2:
                    Assert.Equal("https://example.test", root.GetProperty("origin").GetString());
                    Assert.Equal("success", root.GetProperty("outcome").GetString());
                    Assert.Equal(17, root.GetProperty("ms").GetInt64());
                    break;
                case 3:
                    Assert.Equal("item-id", root.GetProperty("Id").GetString());
                    Assert.Equal("one", root.GetProperty("source").GetString());
                    Assert.Equal("two", root.GetProperty("target").GetString());
                    break;
                case 4:
                    Assert.Equal("check-name", root.GetProperty("hotkey").GetString());
                    break;
                case 5:
                    Assert.Equal(456.7, root.GetProperty("processToReadyMs").GetDouble());
                    Assert.False(root.GetProperty("IsReadOnly").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, root.GetProperty("Notice").ValueKind);
                    break;
                case 6:
                    Assert.Equal(2, root.GetProperty("Formats").GetArrayLength());
                    Assert.Equal(2, root.GetProperty("paths").GetInt32());
                    break;
            }
        }
    }

    [Fact]
    public async Task UnwritableDirectoryDisablesFileLoggingAndRecordsOneFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string blocked = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "fixture");
        try
        {
            var diagnostics = new LoggingDiagnostics();
            var log = SessionLogging.Create(blocked, diagnostics);
            using (var factory = new SerilogLoggerFactory(log, dispose: false))
                for (int i = 0; i < 20; i++) factory.CreateLogger<LoggingTests>().HotkeyChanged("unwritable");
            await log.DisposeAsync();
            Assert.NotNull(diagnostics.Failure);
            var first = diagnostics.Failure;
            diagnostics.RecordFailure("another failure");
            Assert.Same(first, diagnostics.Failure);
            using var json = JsonDocument.Parse(TestLogging.Clef(first));
            Assert.Equal("log.fallback", json.RootElement.GetProperty("EventId").GetProperty("Name").GetString());
            Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("Pid").GetInt32());
            Assert.Equal(diagnostics.Session, json.RootElement.GetProperty("Session").GetString());
            Assert.Equal("fixture", await File.ReadAllTextAsync(blocked));
        }
        finally { File.Delete(blocked); Directory.Delete(directory); }
    }

    [Fact]
    public async Task FileSinkRetainsTwentyFilesAndDrainsCompactJsonAtExit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Swiftdock-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (int i = 1; i <= 25; i++)
                await File.WriteAllTextAsync(Path.Combine(directory, $"swiftdock-{DateTime.Today.AddDays(-i):yyyyMMdd}.jsonl"), "{}\n");
            string foreign = Path.Combine(directory, "other.jsonl");
            await File.WriteAllTextAsync(foreign, "keep");
            var diagnostics = new LoggingDiagnostics();
            var log = SessionLogging.Create(directory, diagnostics);
            using (var factory = new SerilogLoggerFactory(log, dispose: false)) factory.CreateLogger<LoggingTests>().SessionEnding(true);
            await log.DisposeAsync();
            Assert.Null(diagnostics.Failure);
            Assert.Equal(20, Directory.GetFiles(directory, "swiftdock-*.jsonl").Length);
            Assert.False(File.Exists(Path.Combine(directory, $"swiftdock-{DateTime.Today.AddDays(-20):yyyyMMdd}.jsonl")));
            Assert.Equal("keep", await File.ReadAllTextAsync(foreign));
            var entry = Assert.Single(ReadSession(directory, diagnostics.Session));
            Assert.True(entry.GetProperty("saved").GetBoolean());
            Assert.Equal("session-ending", entry.GetProperty("EventId").GetProperty("Name").GetString());
            Assert.Equal(Environment.ProcessId, entry.GetProperty("Pid").GetInt32());
            Assert.True(Guid.TryParse(entry.GetProperty("Session").GetString(), out _));
        }
        finally { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData("Cannot open https://example.test/private?token=secret", "Cannot open https://example.test")]
    [InlineData("No URL here", "No URL here")]
    public void ShellMessagesCannotLeakUrlPathsOrQueries(string input, string expected) => Assert.Equal(expected, LogPrivacy.Message(input));
}
