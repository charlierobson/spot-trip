using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public interface ITrackRecorder
{
    event EventHandler<RecorderStateChangedEventArgs>? StateChanged;
    event EventHandler<RecorderProgressUpdatedEventArgs>? ProgressUpdated;
    bool IsAvailable { get; }
    RecorderOptions Options { get; }
    IReadOnlyList<string> GetAvailableDevices();
    void Configure(RecorderOptions options);
    Task OnBeforePlayAsync(TrackPlaybackInfo info);
    void OnAbort();
}