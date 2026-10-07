using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SpotifyRemote.Models;
using SpotifyRemote.Services;

namespace SpotifyRemote.Controllers;

public class PlayerController : Controller
{
    private readonly SpotifyApiService _api;
    private readonly SpotifyAuthService _auth;
    private readonly CaffeinateService _caffeinate;
    private readonly ITrackRecorder? _trackRecorder;
    private readonly ILogger<PlayerController> _log;

    public PlayerController(SpotifyApiService api, SpotifyAuthService auth,
        CaffeinateService caffeinate, ILogger<PlayerController> log,
        ITrackRecorder? trackRecorder = null)
    {
        _api = api;
        _auth = auth;
        _caffeinate = caffeinate;
        _log = log;
        _trackRecorder = trackRecorder;
    }

    [HttpGet("/player")]
    public async Task<IActionResult> Index()
    {
        var token = await GetValidTokenAsync();
        if (token == null) return RedirectToAction("Index", "Home");

        var playlists = await _api.GetPlaylistsAsync(token);
        var playback = await _api.GetPlaybackStateAsync(token);

        var vm = new PlayerViewModel
        {
            Playlists = playlists,
            Playback = playback.Playback,
            Queue = HttpContext.Session.GetObject<ManagedQueue>("managed_queue")
        };

        return View(vm);
    }

    [HttpGet("/player/playlist/{id}")]
    public async Task<IActionResult> Playlist(string id)
    {
        var token = await GetValidTokenAsync();
        if (token == null) return RedirectToAction("Index", "Home");

        var playlists = await _api.GetPlaylistsAsync(token);
        var tracks = await _api.GetPlaylistTracksAsync(token, id);
        var playback = await _api.GetPlaybackStateAsync(token);

        var vm = new PlayerViewModel
        {
            Playlists = playlists,
            PlaylistTracks = tracks,
            ActivePlaylistId = id,
            Playback = playback.Playback,
            Queue = HttpContext.Session.GetObject<ManagedQueue>("managed_queue")
        };

        return View("Index", vm);
    }

    [HttpPost("/player/play-playlist")]
    public async Task<IActionResult> PlayPlaylist(
        [FromForm] string playlistUri,
        [FromForm] string playlistId,
        [FromForm] int offset = 0)
    {
        var token = await GetValidTokenAsync();
        if (token != null)
            await _api.PlayPlaylistAsync(token, playlistUri, offset);

        return RedirectToAction("Playlist", new { id = playlistId });
    }

    [HttpPost("/player/play-track")]
    public async Task<IActionResult> PlayTrack(
        [FromForm] string playlistUri,
        [FromForm] string trackUri,
        [FromForm] string playlistId,
        [FromForm] string? trackId = null,
        [FromForm] string? trackName = null,
        [FromForm] string? artists = null,
        [FromForm] string? album = null,
        [FromForm] string? imageUrl = null,
        [FromForm] int durationMs = 0,
        [FromForm] int trackNumber = 0)
    {
        var token = await GetValidTokenAsync();
        if (token != null)
        {
            var info = new TrackPlaybackInfo
            {
                TrackId = trackId ?? "",
                TrackUri = trackUri,
                TrackName = trackName ?? "",
                Artists = artists ?? "",
                Album = album ?? "",
                DurationMs = durationMs,
                TrackNumber = trackNumber,
                ImageUrl = imageUrl,
                PlaylistUri = playlistUri,
                PlaylistId = playlistId
            };
            await _api.PlayTrackAsync(token, playlistUri, trackUri, info);
        }

        return RedirectToAction("Playlist", new { id = playlistId });
    }

    [HttpPost("/player/open-recorder-folder")]
    public IActionResult OpenRecorderFolder([FromForm] string returnPlaylistId = "")
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var recorderDir = Path.Combine(home, "ripped");

        try
        {
            if (!Directory.Exists(recorderDir))
                Directory.CreateDirectory(recorderDir);

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", recorderDir);
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start("explorer.exe", recorderDir);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", recorderDir);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unable to open recorder output folder: {Path}", recorderDir);
        }

        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    [HttpPost("/player/pause")]
    public async Task<IActionResult> Pause([FromForm] string returnPlaylistId = "")
    {
        var token = await GetValidTokenAsync();
        if (token != null) await _api.PauseAsync(token);
        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    [HttpPost("/player/resume")]
    public async Task<IActionResult> Resume([FromForm] string returnPlaylistId = "")
    {
        var token = await GetValidTokenAsync();
        if (token != null) await _api.ResumeAsync(token);
        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    [HttpPost("/player/next")]
    public async Task<IActionResult> Next([FromForm] string returnPlaylistId = "")
    {
        var token = await GetValidTokenAsync();
        if (token != null) await _api.NextTrackAsync(token);
        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    [HttpPost("/player/previous")]
    public async Task<IActionResult> Previous([FromForm] string returnPlaylistId = "")
    {
        var token = await GetValidTokenAsync();
        if (token != null) await _api.PreviousTrackAsync(token);
        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    [HttpPost("/player/volume")]
    public async Task<IActionResult> Volume(
        [FromForm] int volumePercent,
        [FromForm] string returnPlaylistId = "")
    {
        var token = await GetValidTokenAsync();
        if (token != null)
            await _api.SetVolumeAsync(token, Math.Clamp(volumePercent, 0, 100));
        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    [HttpPost("/player/start-queue")]
    public async Task<IActionResult> StartQueue(
        [FromForm] string tracksJson,
        [FromForm] string fileExtension = "flac",
        [FromForm] bool skipExistingFiles = false,
        [FromForm] string returnPlaylistId = "")
    {
        var token = await GetValidTokenAsync();
        if (token == null) return RedirectToAction("Index", "Home");

        var tracks = JsonSerializer.Deserialize<List<TrackPlaybackInfo>>(tracksJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (tracks == null || tracks.Count == 0)
            return returnPlaylistId != ""
                ? RedirectToAction("Playlist", new { id = returnPlaylistId })
                : RedirectToAction("Index");

        var ext = fileExtension == "wav" ? "wav" : "flac";
        foreach (var t in tracks)
        {
            t.FileExtension = ext;
            t.SkipIfExists = skipExistingFiles;
        }
        var queue = new ManagedQueue { Tracks = tracks, CurrentIndex = 0 };
        _caffeinate.Start();
        if (!await _api.PlaySingleTrackAsync(token, queue.Current!))
        {
            _trackRecorder?.OnAbort();
            _caffeinate.Stop();
            _log.LogError("Queue could not start because Spotify playback failed.");
            return returnPlaylistId != ""
                ? RedirectToAction("Playlist", new { id = returnPlaylistId })
                : RedirectToAction("Index");
        }
        HttpContext.Session.SetObject("managed_queue", queue);

        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    [HttpPost("/player/advance")]
    public async Task<IActionResult> Advance()
    {
        var token = await GetValidTokenAsync();
        var queue = HttpContext.Session.GetObject<ManagedQueue>("managed_queue");

        if (token == null || queue == null)
            return Json(new { complete = true });

        queue.CurrentIndex++;

        if (queue.IsComplete)
        {
            HttpContext.Session.Remove("managed_queue");
            _caffeinate.Stop();
            _log.LogInformation("Queue complete — {Total} track(s) processed.", queue.Total);
            return Json(new { complete = true });
        }

        if (!await _api.PlaySingleTrackAsync(token, queue.Current!))
        {
            HttpContext.Session.Remove("managed_queue");
            _trackRecorder?.OnAbort();
            _caffeinate.Stop();
            _log.LogError("Queue stopped because Spotify playback failed for {TrackName}.",
                queue.Current!.TrackName);
            return Json(new { complete = true });
        }
        HttpContext.Session.SetObject("managed_queue", queue);

        return Json(new { complete = false, expectedUri = queue.Current!.TrackUri });
    }

    [HttpPost("/player/abort")]
    public async Task<IActionResult> Abort([FromForm] string returnPlaylistId = "")
    {
        var token = await GetValidTokenAsync();
        HttpContext.Session.Remove("managed_queue");
        _trackRecorder?.OnAbort();
        _caffeinate.Stop();
        if (token != null) await _api.PauseAsync(token);

        return returnPlaylistId != ""
            ? RedirectToAction("Playlist", new { id = returnPlaylistId })
            : RedirectToAction("Index");
    }

    // Polled by JS to update now-playing without a full page reload
    [HttpGet("/player/state")]
    public async Task<IActionResult> State()
    {
        var token = await GetValidTokenAsync();
        if (token == null) return Json(new { authenticated = false });

        var playbackResult = await _api.GetPlaybackStateAsync(token);
        var queue = HttpContext.Session.GetObject<ManagedQueue>("managed_queue");
        var queueActive = queue != null && !queue.IsComplete;

        if (!playbackResult.IsAvailable)
            return Json(new { authenticated = true, stateAvailable = false });

        var playback = playbackResult.Playback;

        if (playback == null)
            return Json(new
            {
                authenticated = true,
                stateAvailable = true,
                playing = false,
                managedQueueActive = queueActive,
                managedExpectedUri = queue?.Current?.TrackUri,
                managedCurrentIndex = queue?.CurrentIndex ?? 0,
                managedTotal = queue?.Total ?? 0,
                managedTimeLeft = ""
            });

        var managedTimeLeftMs = 0;
        var managedTimeLeft = "";
        if (queue != null && !queue.IsComplete)
        {
            if (playback.CurrentTrack != null && queue.Current != null &&
                playback.CurrentTrack.Uri == queue.Current.TrackUri)
            {
                managedTimeLeftMs += Math.Max(0, queue.Current.DurationMs - playback.ProgressMs);
            }
            else if (queue.Current != null)
            {
                managedTimeLeftMs += queue.Current.DurationMs;
            }

            managedTimeLeftMs += queue.Tracks
                .Skip(queue.CurrentIndex + 1)
                .Sum(t => t.DurationMs);

            managedTimeLeft = FormatDuration(managedTimeLeftMs);
        }

        return Json(new
        {
            authenticated = true,
            stateAvailable = true,
            playing = playback.IsPlaying,
            trackName = playback.CurrentTrack?.Name,
            artists = playback.CurrentTrack?.Artists,
            album = playback.CurrentTrack?.Album,
            imageUrl = playback.CurrentTrack?.ImageUrl,
            trackUri = playback.CurrentTrack?.Uri,
            progressMs = playback.ProgressMs,
            durationMs = playback.CurrentTrack?.DurationMs ?? 0,
            device = playback.DeviceName,
            volume = playback.VolumePercent,
            managedQueueActive = queueActive,
            managedExpectedUri = queue?.Current?.TrackUri,
            managedCurrentIndex = queue?.CurrentIndex ?? 0,
            managedTotal = queue?.Total ?? 0,
            managedTimeLeft = managedTimeLeft
        });
    }

    private static string FormatDuration(int totalMs)
    {
        var span = TimeSpan.FromMilliseconds(totalMs);
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}";

        return $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
    }

    private async Task<string?> GetValidTokenAsync()
    {
        var accessToken = HttpContext.Session.GetString("access_token");
        var refreshToken = HttpContext.Session.GetString("refresh_token");
        var expiresAtStr = HttpContext.Session.GetString("expires_at");

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
            return null;

        if (DateTime.TryParse(expiresAtStr, out var expiresAt) &&
            DateTime.UtcNow >= expiresAt)
        {
            var tokens = await _auth.RefreshTokenAsync(refreshToken);
            if (tokens == null) return null;

            accessToken = tokens.AccessToken;
            if (!string.IsNullOrEmpty(tokens.RefreshToken))
                refreshToken = tokens.RefreshToken;

            HttpContext.Session.SetString("access_token", accessToken);
            HttpContext.Session.SetString("refresh_token", refreshToken);
            HttpContext.Session.SetString("expires_at",
                tokens.ExpiresAt.ToString("O"));
        }

        return accessToken;
    }
}
