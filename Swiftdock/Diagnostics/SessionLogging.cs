using System.IO;
using Microsoft.Extensions.Logging;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using Serilog;

namespace Swiftdock.Diagnostics;

// Only the first failure is retained. Never send a sink failure back through that sink.
internal sealed class LoggingDiagnostics : ILogEventSink, ILoggingFailureListener
{
    private LogEvent? _failure;
    internal string Session { get; } = Guid.NewGuid().ToString("N");
    internal LogEvent? Failure => Volatile.Read(ref _failure);
    public void Emit(LogEvent logEvent) => Interlocked.CompareExchange(ref _failure, logEvent, null);

    internal void RecordFailure(string message)
    {
        if (Failure is not null) return;
        using var factory = new SerilogLoggerFactory(new LoggerConfiguration()
            .Enrich.WithProperty("Pid", Environment.ProcessId).Enrich.WithProperty("Session", Session)
            .WriteTo.Sink(this).CreateLogger(), dispose: true);
        factory.CreateLogger<LoggingDiagnostics>().LogFallback(message);
    }

    public void OnLoggingFailed(object sender, LoggingFailureKind kind, string message,
        IReadOnlyCollection<LogEvent>? events, Exception? exception)
    {
        if (kind != LoggingFailureKind.Temporary) RecordFailure(exception?.GetType().Name ?? message);
    }
}

internal static class SessionLogging
{
    internal static Logger Create(string directory, LoggingDiagnostics diagnostics, Func<string, ILogEventSink>? createFile = null) => new LoggerConfiguration()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.WithProperty("Session", diagnostics.Session)
        .Enrich.WithProperty("Pid", Environment.ProcessId)
        .WriteTo.Sink(new SessionFileSink(directory, diagnostics, createFile ?? CreateFileSink), new BatchingOptions
        {
            BatchSizeLimit = 64, BufferingTimeLimit = TimeSpan.FromMilliseconds(250), QueueLimit = 512,
            EagerlyEmitFirstEvent = true
        }).CreateLogger();

    // Serilog owns the bounded queue and its worker. File opening, formatting, retention,
    // flushing and disposal happen there, never in a WPF logging call.
    private static ILogEventSink CreateFileSink(string directory) => LoggerSinkConfiguration.CreateSink(write => write.File(
        new CompactJsonFormatter(), Path.Combine(directory, "swiftdock-.jsonl"), rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 4 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 20, shared: true));

    private sealed class SessionFileSink(string directory, LoggingDiagnostics diagnostics, Func<string, ILogEventSink> createFile) : IBatchedLogEventSink, IDisposable
    {
        private ILogEventSink? _file;

        public Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
        {
            if (diagnostics.Failure is not null) return Task.CompletedTask;
            try
            {
                if (_file is null)
                {
                    _file = createFile(directory);
                    if (_file is ISetLoggingFailureListener failures) failures.SetFailureListener(diagnostics);
                }
                foreach (var entry in batch)
                {
                    _file.Emit(entry);
                    if (diagnostics.Failure is not null) break;
                }
            }
            catch (Exception ex) { diagnostics.RecordFailure(ex.GetType().Name); }
            return Task.CompletedTask;
        }

        public Task OnEmptyBatchAsync() => Task.CompletedTask;

        public void Dispose()
        {
            try { (_file as IDisposable)?.Dispose(); }
            catch (Exception ex) { diagnostics.RecordFailure(ex.GetType().Name); }
        }
    }
}
