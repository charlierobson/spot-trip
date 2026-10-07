using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public sealed class ProcessTrackRecorder(IConfiguration config) : ITrackRecorder
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly string _audioDevice = config["Recording:AudioDevice"] ?? "BlackHole 2ch";
    private readonly bool _skipExistingFiles = config.GetValue<bool?>("Recording:SkipExistingFiles") ?? false;
    private readonly object _processLock = new();
    private readonly object _stateLock = new();
    private Process? _currentProcess;
    private string? _currentTrackName;
    private string? _lastStateTrackName;
    private TrackRecorderState? _lastState;

    public event EventHandler<RecorderStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecorderProgressUpdatedEventArgs>? ProgressUpdated;

    public bool IsAvailable => File.Exists(RecorderPath);

    private static string RecorderPath =>
        Path.Combine(AppContext.BaseDirectory, "Tools", "recorder");

    public async Task OnBeforePlayAsync(TrackPlaybackInfo info)
    {
        await StopCurrentAsync();
        PublishState(info.TrackName, TrackRecorderState.Starting);

        string? outputPath = null;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var playlistFolder = Path.Combine(home, "ripped", SanitizeFilename(info.PlaylistName));
            Directory.CreateDirectory(playlistFolder);
            outputPath = Path.Combine(playlistFolder,
                $"{SanitizeFilename(info.Artists)} - {SanitizeFilename(info.TrackName)}.{info.FileExtension}");

            if ((_skipExistingFiles || info.SkipIfExists) && File.Exists(outputPath))
            {
                PublishState(info.TrackName, TrackRecorderState.Skipped,
                    "Output already exists.", outputPath);
                return;
            }

            EnsureExecutable(RecorderPath);
            var startInfo = new ProcessStartInfo(RecorderPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add((info.DurationMs / 1000).ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(_audioDevice);

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Recorder process could not be started.");
            lock (_processLock)
            {
                _currentProcess = process;
                _currentTrackName = info.TrackName;
            }

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = PumpStandardOutputAsync(process.StandardOutput, info, outputPath, ready);
            _ = PumpStandardErrorAsync(process.StandardError, info.TrackName, outputPath);
            if (await Task.WhenAny(ready.Task, Task.Delay(ReadyTimeout)) != ready.Task)
                PublishState(info.TrackName, TrackRecorderState.ReadyTimeout,
                    $"Recorder did not signal ready within {ReadyTimeout}.", outputPath);
        }
        catch (Exception ex)
        {
            PublishState(info.TrackName, TrackRecorderState.Failed, ex.Message, outputPath);
            throw;
        }
    }

    public void OnAbort()
    {
        StopCurrentAsync(aborted: true).GetAwaiter().GetResult();
        var name = Path.GetFileNameWithoutExtension(RecorderPath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            try { process.Kill(); } catch { }
            process.Dispose();
        }
    }

    private async Task PumpStandardOutputAsync(
        StreamReader reader, TrackPlaybackInfo info, string outputPath, TaskCompletionSource ready)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("Device:", StringComparison.OrdinalIgnoreCase))
                {
                    PublishState(info.TrackName, TrackRecorderState.Ready, line, outputPath);
                    ready.TrySetResult();
                }
                else if (TryParseProgress(line, out var state, out var elapsed))
                {
                    PublishState(info.TrackName, state, outputPath: outputPath);
                    ProgressUpdated?.Invoke(this, new RecorderProgressUpdatedEventArgs(
                        info.TrackName, elapsed,
                        info.DurationMs > 0 ? TimeSpan.FromMilliseconds(info.DurationMs) : null));
                }
                else if (line.StartsWith("Saving", StringComparison.OrdinalIgnoreCase))
                    PublishState(info.TrackName, TrackRecorderState.Saving, line, outputPath);
                else if (line.StartsWith("Saved:", StringComparison.OrdinalIgnoreCase))
                    PublishState(info.TrackName, TrackRecorderState.Saved, line, outputPath);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { ready.TrySetResult(); }
    }

    private async Task PumpStandardErrorAsync(StreamReader reader, string trackName, string outputPath)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                if (!string.IsNullOrWhiteSpace(line))
                    PublishState(trackName, TrackRecorderState.Failed, line, outputPath);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void PublishState(
        string trackName, TrackRecorderState state, string? message = null, string? outputPath = null)
    {
        lock (_stateLock)
        {
            if (_lastStateTrackName == trackName && _lastState == state) return;
            _lastStateTrackName = trackName;
            _lastState = state;
        }
        StateChanged?.Invoke(this,
            new RecorderStateChangedEventArgs(trackName, state, message, outputPath));
    }

    private async Task StopCurrentAsync(bool aborted = false)
    {
        Process? process;
        string? trackName;
        lock (_processLock)
        {
            process = _currentProcess;
            trackName = _currentTrackName;
            _currentProcess = null;
            _currentTrackName = null;
        }
        if (process == null) return;

        try
        {
            if (process.HasExited) return;
            if (!string.IsNullOrEmpty(trackName))
                PublishState(trackName, TrackRecorderState.Stopping);
            using var cts = new CancellationTokenSource(StopTimeout);
            try
            {
                await process.StandardInput.WriteLineAsync("STOP");
                await process.StandardInput.FlushAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
        finally { process.Dispose(); }

        if (!string.IsNullOrEmpty(trackName))
            PublishState(trackName, aborted ? TrackRecorderState.Aborted : TrackRecorderState.Stopped);
    }

    private static bool TryParseProgress(
        string line, out TrackRecorderState state, out TimeSpan elapsed)
    {
        state = default;
        elapsed = default;
        var closeBracket = line.IndexOf(']');
        if (line.Length == 0 || line[0] != '[' || closeBracket < 0) return false;
        var label = line[1..closeBracket].Trim();
        state = label switch
        {
            "Recording" => TrackRecorderState.Recording,
            "Finishing" => TrackRecorderState.Finishing,
            "Done" => TrackRecorderState.Completed,
            _ => default
        };
        if (label is not ("Recording" or "Finishing" or "Done")) return false;
        return TryParseElapsed(line[(closeBracket + 1)..].Trim(), out elapsed);
    }

    private static bool TryParseElapsed(string value, out TimeSpan elapsed)
    {
        elapsed = default;
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3 ||
            !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0 || seconds >= 60)
            return false;

        if (!int.TryParse(parts[^2], NumberStyles.None,
                CultureInfo.InvariantCulture, out var minutes) || minutes < 0)
            return false;

        if (parts.Length == 2)
        {
            elapsed = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (minutes >= 60 ||
            !int.TryParse(parts[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out var hours) || hours < 0)
            return false;

        elapsed = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) +
                  TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static string SanitizeFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "_";
        var invalid = new string(Path.GetInvalidFileNameChars());
        return Regex.Replace(input, $"[{Regex.Escape(invalid)}]", "_").Trim().Trim('.');
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var chmod = new ProcessStartInfo("chmod") { UseShellExecute = false };
        chmod.ArgumentList.Add("+x");
        chmod.ArgumentList.Add(path);
        Process.Start(chmod)?.WaitForExit();
    }
}