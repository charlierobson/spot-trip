using System.Runtime.InteropServices;

namespace SpotifyRemote.Services;

public sealed class WinSleepInhibitor : ISleepInhibitor
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private readonly object _lock = new();
    private Thread? _thread;
    private ManualResetEventSlim? _release;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    // The execution state is per-thread, so it is held by a dedicated thread until released.
    public void Start()
    {
        lock (_lock)
        {
            if (_thread != null) return;
            var release = new ManualResetEventSlim();
            _release = release;
            _thread = new Thread(() =>
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
                release.Wait();
                SetThreadExecutionState(EsContinuous);
            }) { IsBackground = true, Name = "SleepInhibitor" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_thread == null) return;
            _release!.Set();
            _thread.Join();
            _release.Dispose();
            _thread = null;
            _release = null;
        }
    }

    public void Dispose() => Stop();
}
