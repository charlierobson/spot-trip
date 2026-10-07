namespace SpotifyRemote.Services;

public sealed class ConsoleTrackLogger : IDisposable
{
    private readonly ITrackRecorder _recorder;
    private readonly IRecorderLogPublisher _logPublisher;
    private readonly ILogger<ConsoleTrackLogger> _log;
    private readonly object _progressLock = new();
    private bool _progressLineOpen;

    public ConsoleTrackLogger(
        ITrackRecorder recorder,
        IRecorderLogPublisher logPublisher,
        ILogger<ConsoleTrackLogger> log)
    {
        _recorder = recorder;
        _logPublisher = logPublisher;
        _log = log;
        _recorder.StateChanged += OnStateChanged;
        _recorder.ProgressUpdated += OnProgressUpdated;
    }

    public void Dispose()
    {
        _recorder.StateChanged -= OnStateChanged;
        _recorder.ProgressUpdated -= OnProgressUpdated;
    }

    private void OnStateChanged(object? sender, RecorderStateChangedEventArgs e)
    {
        var line = string.IsNullOrWhiteSpace(e.Message)
            ? $"[{e.State}] {e.TrackName}"
            : $"[{e.State}] {e.TrackName} - {e.Message}";

        lock (_progressLock)
        {
            if (_progressLineOpen)
            {
                Console.WriteLine();
                _progressLineOpen = false;
            }

            if (e.State == TrackRecorderState.Failed)
                _log.LogError("{Line}", line);
            else
                _log.LogInformation("{Line}", line);
        }
    }

    private void OnProgressUpdated(object? sender, RecorderProgressUpdatedEventArgs e)
    {
        var expectedDuration = e.ExpectedDuration is TimeSpan duration
            ? $" / {FormatDuration(duration)}"
            : "";
        var line = $"[Progress] {e.TrackName} - {FormatDuration(e.Elapsed)}{expectedDuration}";
        _logPublisher.Publish(line);
        lock (_progressLock)
        {
            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(line);
                return;
            }

            Console.Write($"\r\u001b[2K{line}");
            Console.Out.Flush();
            _progressLineOpen = true;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";

        return $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
    }
}
