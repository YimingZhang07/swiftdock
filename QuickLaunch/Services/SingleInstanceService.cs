using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

public sealed class SingleInstanceService : IAsyncDisposable
{
    private Mutex? _lifetime;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;
    private readonly string _pipeName;
    private readonly Action _showRequested;
    private readonly Action? _exitRequested;
    private readonly Func<bool> _acceptsRequests;
    public bool IsPrimary { get; private set; }
    public string Identity { get; }

    public SingleInstanceService(string configDirectory, Action showRequested, Func<bool>? acceptsRequests = null, Action? exitRequested = null)
    {
        Identity = GetIdentity(configDirectory);
        _pipeName = "QuickLaunch_" + Identity;
        _showRequested = showRequested;
        _exitRequested = exitRequested;
        _acceptsRequests = acceptsRequests ?? (() => true);
        TryBecomePrimary();
    }

    internal static string GetIdentity(string configDirectory)
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string sid = user.User?.Value ?? throw new InvalidOperationException("无法确定当前用户 SID。");
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory)).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
        return $"{sid}_{process.SessionId}_{hash}";
    }

    private bool TryBecomePrimary()
    {
        if (IsPrimary) return true;
        // Keep a named object alive, without thread-affine ownership across async calls.
        // Secondaries must not keep the object alive after the primary exits.
        var lifetime = new Mutex(initiallyOwned: false, "Local\\QuickLaunch_" + Identity, out bool created);
        if (!created) { lifetime.Dispose(); return false; }
        _lifetime = lifetime;
        IsPrimary = true;
        _server = Task.Run(ServeAsync);
        return true;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                var token = requestTimeout.Token;
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), token);
                var command = new byte[1];
                await pipe.ReadExactlyAsync(command, token);
                await HandleRequestAsync(command[0], pipe, _showRequested, _exitRequested, _acceptsRequests, token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            { /* A failed/disconnected client must not terminate the listener. */ }
        }
    }

    internal static async Task HandleRequestAsync(byte command, Stream response, Action showRequested, Action? exitRequested,
        Func<bool> acceptsRequests, CancellationToken cancellation = default)
    {
        bool accepted = (command == 1 || command == 2 && exitRequested is not null) && acceptsRequests();
        if (accepted && command == 1) showRequested();
        await response.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, cancellation);
        await response.FlushAsync(cancellation);
        // Exit can dispose this listener immediately. Send ACK before queuing shutdown.
        if (accepted && command == 2) exitRequested!();
    }

    public static async Task<int> RequestExitAsync(string configDirectory, TimeSpan? timeout = null)
    {
        // Client only: an exit command must never acquire the mutex or start a replacement.
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            return await SendRequestAsync("QuickLaunch_" + GetIdentity(configDirectory), 2, deadline.Token) ? 0 : 2;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { return 2; }
    }

    private static async Task<bool> SendRequestAsync(string pipeName, byte command, CancellationToken cancellation)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(command == 1 ? 300 : 2000, cancellation);
        var pidBytes = new byte[4];
        await pipe.ReadExactlyAsync(pidBytes, cancellation);
        int serverPid = BitConverter.ToInt32(pidBytes);
        if (serverPid <= 0) throw new IOException("已有实例返回无效 PID。");
        if (command == 1) NativeMethods.AllowSetForegroundWindow((uint)serverPid);
        await pipe.WriteAsync(new byte[] { command }, cancellation);
        var ack = new byte[1];
        await pipe.ReadExactlyAsync(ack, cancellation);
        return ack[0] == 1;
    }

    public async Task NotifyPrimaryAsync(TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                if (await SendRequestAsync(_pipeName, 1, deadline.Token)) return;
                throw new IOException("已有实例未确认显示请求。");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                // Primary may have closed between process launch and IPC; atomically take its place.
                if (TryBecomePrimary()) return;
                await Task.Delay(80, deadline.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_server is not null) await _server.ConfigureAwait(false);
        _lifetime?.Dispose();
        _stop.Dispose();
    }
}
