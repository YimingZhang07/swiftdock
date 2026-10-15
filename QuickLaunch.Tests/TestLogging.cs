using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuickLaunch.Services;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using Serilog;

namespace QuickLaunch.Tests;

internal sealed class TestLogging : ILogEventSink, IAsyncDisposable
{
    private readonly SerilogLoggerFactory _factory;
    private readonly List<ServiceProvider> _icons = [];
    internal ConcurrentQueue<LogEvent> Events { get; } = new();

    internal TestLogging() => _factory = new(new LoggerConfiguration().WriteTo.Sink(this).CreateLogger(), dispose: true);
    public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    internal ILogger<T> Logger<T>() => _factory.CreateLogger<T>();

    internal IconService CreateIcons(string directory, HttpMessageHandler? handler = null)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<ILoggerFactory>(_factory)
            .AddIconServices(directory, handler is null ? null : () => handler).BuildServiceProvider();
        _icons.Add(services);
        return services.GetRequiredService<IconService>();
    }

    internal static string Clef(LogEvent entry)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new CompactJsonFormatter().Format(entry, writer);
        return writer.ToString();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var icons in _icons) icons.Dispose();
        _icons.Clear();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
