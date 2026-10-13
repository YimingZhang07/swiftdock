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
    private readonly Func<bool> _acceptsRequests;
    public bool IsPrimary { get; private set; }
    public string Identity { get; }

    public SingleInstanceService(string configDirectory, Action showRequested, Func<bool>? acceptsRequests = null)
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string sid = user.User?.Value ?? throw new InvalidOperationException("无法确定当前用户 SID。");
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory)).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
        Identity = $"{sid}_{process.SessionId}_{hash}";
        _pipeName = "QuickLaunch_" + Identity;
        _showRequested = showRequested;
        _acceptsRequests = acceptsRequests ?? (() => true);
        TryBecomePrimary();
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
                if (command[0] != 1) continue;
                if (!_acceptsRequests()) { await pipe.WriteAsync(new byte[] { 0 }, token); continue; }
                _showRequested(); // Queue onto the UI dispatcher; ACK means accepted, not yet rendered.
                await pipe.WriteAsync(new byte[] { 1 }, token);
                await pipe.FlushAsync(token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            { /* A failed/disconnected client must not terminate the listener. */ }
        }
    }

    public async Task NotifyPrimaryAsync(TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(300, deadline.Token);
                var pidBytes = new byte[4];
                await pipe.ReadExactlyAsync(pidBytes, deadline.Token);
                int serverPid = BitConverter.ToInt32(pidBytes);
                if (serverPid <= 0) throw new IOException("已有实例返回无效 PID。");
                NativeMethods.AllowSetForegroundWindow((uint)serverPid);
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
                var ack = new byte[1];
                await pipe.ReadExactlyAsync(ack, deadline.Token);
                if (ack[0] == 1) return;
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
