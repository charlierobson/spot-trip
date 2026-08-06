using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public interface ITrackPlaybackHandler
{
    Task OnBeforePlayAsync(TrackPlaybackInfo info);
    void OnAbort();
}
