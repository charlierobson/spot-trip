using System.Diagnostics;
using System.Text.RegularExpressions;
using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public class ConsoleTrackLogger(IConfiguration config, ILogger<ConsoleTrackLogger> log) : ITrackPlaybackHandler
{
    private const string ReadyKeyword = "Device:";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly string _audioDevice = config["Recording:AudioDevice"] ?? "BlackHole 2ch";
    private readonly bool _skipExistingFiles = config.GetValue<bool?>("Recording:SkipExistingFiles") ?? false;
    private readonly object _processLock = new();
    private Process? _currentProcess;

    public async Task OnBeforePlayAsync(TrackPlaybackInfo info)
    {
        await StopCurrentAsync();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var playlistFolder = Path.Combine(home, "ripped", SanitizeFilename(info.PlaylistName));
        Directory.CreateDirectory(playlistFolder);

        string exe = Path.Combine(AppContext.BaseDirectory, "Tools", "recorder");
        EnsureExecutable(exe);

        string filename = Path.Combine(playlistFolder, $"{SanitizeFilename(info.Artists)} - {SanitizeFilename(info.TrackName)}.{info.FileExtension}");

        if ((_skipExistingFiles || info.SkipIfExists) && File.Exists(filename))
        {
            log.LogInformation("Skipping recorder for {Track} because output already exists: {File}",
                info.TrackName, filename);
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        psi.ArgumentList.Add(filename);
        psi.ArgumentList.Add((info.DurationMs / 1000).ToString());
        psi.ArgumentList.Add(_audioDevice);

        log.LogInformation("Starting recorder for {Track} -> {File} on {Device}",
            info.TrackName, filename, _audioDevice);

        var process = Process.Start(psi)!;
        lock (_processLock) { _currentProcess = process; }

        // Drain stdout/stderr for the whole life of the process and log every
        // line to file — nothing else reads these pipes now that the process
        // isn't attached to a visible terminal, and an undrained pipe would
        // eventually block the recorder's writes on a long track.
        var readyTcs = new TaskCompletionSource();
        _ = PumpStreamAsync(process.StandardOutput, "recorder", readyTcs);
        _ = PumpStreamAsync(process.StandardError, "recorder-err", readyTcs: null);

        var completed = await Task.WhenAny(readyTcs.Task, Task.Delay(ReadyTimeout));
        if (completed != readyTcs.Task)
            log.LogWarning("Recorder for {Track} did not signal ready within {Timeout} — playing anyway",
                info.TrackName, ReadyTimeout);
    }

    private async Task PumpStreamAsync(StreamReader reader, string tag, TaskCompletionSource? readyTcs)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                log.LogInformation("[{Tag}] {Line}", tag.Trim(), line.Trim());
                if (readyTcs != null && !readyTcs.Task.IsCompleted && line.Contains(ReadyKeyword))
                    readyTcs.TrySetResult();
            }
        }
        catch
        {
            // process was killed/stopped out from under us — nothing left to read
        }
        finally
        {
            readyTcs?.TrySetResult();
        }
    }

    public void OnAbort()
    {
        StopCurrentAsync().GetAwaiter().GetResult();

        // Safety net for any orphaned instances outside our tracking.
        var name = Path.GetFileNameWithoutExtension(
            Path.Combine(AppContext.BaseDirectory, "Tools", "recorder"));
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(); } catch { }
            p.Dispose();
        }
    }

    // Gracefully stops the previously-tracked recorder (if still running) by
    // sending it an explicit STOP command and waiting for it to exit and
    // flush its output file, falling back to Kill() if it doesn't respond.
    private async Task StopCurrentAsync()
    {
        Process? process;
        lock (_processLock)
        {
            process = _currentProcess;
            _currentProcess = null;
        }

        if (process == null) return;

        try
        {
            if (process.HasExited) return;

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
        finally
        {
            process.Dispose();
        }
    }

    private static string SanitizeFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "_";
        var invalid = new string(Path.GetInvalidFileNameChars());
        var sanitized = Regex.Replace(input, $"[{Regex.Escape(invalid)}]", "_");
        return sanitized.Trim().Trim('.');
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("chmod", $"+x \"{path}\"")
                { UseShellExecute = false })?.WaitForExit();
    }
}
