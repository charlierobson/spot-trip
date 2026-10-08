namespace SpotifyRemote.Services;

public interface ISleepInhibitor : IDisposable
{
    void Start();
    void Stop();
}
