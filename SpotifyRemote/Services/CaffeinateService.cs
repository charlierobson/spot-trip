using System.Diagnostics;

namespace SpotifyRemote.Services;

public class CaffeinateService : IDisposable
{
    private Process? _process;

    public void Start()
    {
        if (_process != null) return;
        _process = Process.Start(new ProcessStartInfo("caffeinate")
        {
            Arguments = "-di",
            UseShellExecute = false
        });
    }

    public void Stop()
    {
        if (_process == null) return;
        try { _process.Kill(); } catch { }
        _process.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
