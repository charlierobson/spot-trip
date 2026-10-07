using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public interface ITrackRecorder
{
    event EventHandler<RecorderStateChangedEventArgs>? StateChanged;
    event EventHandler<RecorderProgressUpdatedEventArgs>? ProgressUpdated;
    bool IsAvailable { get; }
    Task OnBeforePlayAsync(TrackPlaybackInfo info);
    void OnAbort();
}