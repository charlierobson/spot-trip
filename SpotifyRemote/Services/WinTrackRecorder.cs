using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public sealed class WinTrackRecorder(IConfiguration config) : ITrackRecorder
{
    public event EventHandler<RecorderStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecorderProgressUpdatedEventArgs>? ProgressUpdated;

    public bool IsAvailable => false;

    public RecorderOptions Options { get; private set; } = new();

    public void Configure(RecorderOptions options) => Options = options;

    public IReadOnlyList<string> GetAvailableDevices() => [];

    public Task OnBeforePlayAsync(TrackPlaybackInfo info) =>
        throw new NotImplementedException("Windows recorder is not implemented yet.");

    public void OnAbort() { }
}
