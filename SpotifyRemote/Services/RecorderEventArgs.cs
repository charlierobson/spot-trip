namespace SpotifyRemote.Services;

public enum TrackRecorderState
{
    Starting,
    Ready,
    ReadyTimeout,
    Recording,
    Finishing,
    Completed,
    Saving,
    Saved,
    Skipped,
    Stopping,
    Stopped,
    Aborted,
    Failed
}

public sealed class RecorderStateChangedEventArgs(
    string trackName,
    TrackRecorderState state,
    string? message = null,
    string? outputPath = null) : EventArgs
{
    public string TrackName { get; } = trackName;
    public TrackRecorderState State { get; } = state;
    public string? Message { get; } = message;
    public string? OutputPath { get; } = outputPath;
}

public sealed class RecorderProgressUpdatedEventArgs(
    string trackName,
    TimeSpan elapsed,
    TimeSpan? expectedDuration) : EventArgs
{
    public string TrackName { get; } = trackName;
    public TimeSpan Elapsed { get; } = elapsed;
    public TimeSpan? ExpectedDuration { get; } = expectedDuration;
}