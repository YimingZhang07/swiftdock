using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickLaunch.Interop;

namespace QuickLaunch.Services;

internal sealed class IconThread : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    internal Task<Dispatcher> Ready => _ready.Task;

    internal IconThread()
    {
        var thread = new Thread(() =>
        {
            int result = NativeMethods.OleInitialize(0);
            if (result < 0) { _ready.TrySetException(Marshal.GetExceptionForHR(result)!); return; }
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ready.SetResult(dispatcher);
                if (!_stop.IsCancellationRequested) Dispatcher.Run();
            }
            finally { NativeMethods.OleUninitialize(); }
        }) { IsBackground = true, Name = "QuickLaunch Shell icons" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }

    internal async Task<BitmapSource> RenderFaviconAsync(BitmapSource source, int pixels)
    {
        var dispatcher = await Ready.WaitAsync(_stop.Token).ConfigureAwait(false);
        return await dispatcher.InvokeAsync(() => IconService.RenderFavicon(source, pixels),
            DispatcherPriority.Background, _stop.Token).Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (Ready.IsCompletedSuccessfully) Ready.Result.BeginInvokeShutdown(DispatcherPriority.Send);
        // A blocked third-party Shell extension cannot be force-cancelled; its thread is background.
    }
}
