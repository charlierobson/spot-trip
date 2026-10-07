using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public class SpotifyApiService
{
    private readonly HttpClient _http;
    private readonly ITrackRecorder? _trackRecorder;

    private readonly ILogger<SpotifyApiService> _log;
    private readonly ITokenRefresher _tokenRefresher;
    private readonly IHostApplicationLifetime _lifetime;

    public SpotifyApiService(IHttpClientFactory factory, ILogger<SpotifyApiService> log,
        ITokenRefresher tokenRefresher, IHostApplicationLifetime lifetime,
        ITrackRecorder? trackRecorder = null)
    {
        _http = factory.CreateClient("spotify-api");
        _log = log;
        _tokenRefresher = tokenRefresher;
        _lifetime = lifetime;
        _trackRecorder = trackRecorder;
    }

    public async Task<List<SpotifyPlaylist>> GetPlaylistsAsync(string accessToken)
    {
        var playlists = new List<SpotifyPlaylist>();
        string? url = "https://api.spotify.com/v1/me/playlists?limit=50";

        while (url != null)
        {
            var json = await GetJsonAsync(accessToken, url);
            if (json == null) break;

            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                playlists.Add(ParsePlaylist(item));
            }

            url = json.RootElement.TryGetProperty("next", out var next) &&
                  next.ValueKind != JsonValueKind.Null
                ? next.GetString()
                : null;
        }

        return playlists;
    }

    public async Task<List<SpotifyTrack>> GetPlaylistTracksAsync(
        string accessToken, string playlistId)
    {
        var tracks = new List<SpotifyTrack>();
        string? url = $"https://api.spotify.com/v1/playlists/{playlistId}/items?limit=100&additional_types=track,episode";
        while (url != null)
        {
            var json = await GetJsonAsync(accessToken, url);
            if (json == null) break;

            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!item.TryGetProperty("item", out var t) ||
                    t.ValueKind == JsonValueKind.Null) continue;

                tracks.Add(ParseTrack(t));
            }

            url = json.RootElement.TryGetProperty("next", out var next) &&
                  next.ValueKind != JsonValueKind.Null
                ? next.GetString()
                : null;
        }

        return tracks;
    }

    public async Task<PlaybackStateResult> GetPlaybackStateAsync(string accessToken)
    {
        using var response = await GetResponseAsync(accessToken,
            "https://api.spotify.com/v1/me/player");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return new PlaybackStateResult(true, null);

        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content))
        {
            if (!response.IsSuccessStatusCode)
                _log.LogError("Spotify GET /v1/me/player → {Status}: {Body}",
                    (int)response.StatusCode, content);
            return new PlaybackStateResult(false, null);
        }

        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
            return new PlaybackStateResult(false, null);

        var state = new PlaybackState
        {
            IsPlaying = root.TryGetProperty("is_playing", out var ip) && ip.GetBoolean(),
            ProgressMs = root.TryGetProperty("progress_ms", out var pm)
                ? pm.GetInt32() : 0
        };

        if (root.TryGetProperty("item", out var item) &&
            item.ValueKind != JsonValueKind.Null)
        {
            state.CurrentTrack = ParseTrack(item);
        }

        if (root.TryGetProperty("device", out var device))
        {
            state.DeviceId = device.TryGetProperty("id", out var did)
                ? did.GetString() : null;
            state.DeviceName = device.TryGetProperty("name", out var dn)
                ? dn.GetString() : null;
            state.VolumePercent = device.TryGetProperty("volume_percent", out var vol)
                ? vol.GetInt32() : 0;
        }

        return new PlaybackStateResult(true, state);
    }

    public async Task<string?> GetActiveDeviceIdAsync(string accessToken)
    {
        var json = await GetJsonAsync(accessToken,
            "https://api.spotify.com/v1/me/player/devices");
        if (json == null) return null;

        string? firstId = null;
        foreach (var d in json.RootElement.GetProperty("devices").EnumerateArray())
        {
            var id = d.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (id == null) continue;
            var isActive = d.TryGetProperty("is_active", out var activeEl) && activeEl.GetBoolean();
            if (isActive) return id;
            firstId ??= id;
        }
        return firstId;
    }

    public async Task<bool> PlayPlaylistAsync(
        string accessToken, string playlistUri, int offsetIndex = 0)
    {
        var deviceId = await GetActiveDeviceIdAsync(accessToken);
        var url = PlayUrl(deviceId);
        var payload = new
        {
            context_uri = playlistUri,
            offset = new { position = offsetIndex },
            position_ms = 0
        };
        return await PutAsync(accessToken, url, payload);
    }

    public async Task<bool> PlayTrackAsync(
        string accessToken, string playlistUri, string trackUri,
        TrackPlaybackInfo? info = null)
    {
        if (info != null && _trackRecorder != null)
            await _trackRecorder.OnBeforePlayAsync(info);

        var deviceId = await GetActiveDeviceIdAsync(accessToken);
        var payload = new
        {
            context_uri = playlistUri,
            offset = new { uri = trackUri },
            position_ms = 0
        };
        Func<Task>? onRetry = info != null && _trackRecorder != null
            ? () => _trackRecorder.OnBeforePlayAsync(info)
            : null;
        return await PutAsync(accessToken, PlayUrl(deviceId), payload, onRetry);
    }

    public async Task<bool> PlaySingleTrackAsync(string accessToken, TrackPlaybackInfo info)
    {
        var refreshedToken = await _tokenRefresher.RefreshAsync();
        if (string.IsNullOrEmpty(refreshedToken))
        {
            _log.LogError("Unable to refresh Spotify token before playing {TrackName}.",
                info.TrackName);
            return false;
        }
        accessToken = refreshedToken;

        if (_trackRecorder != null)
            await _trackRecorder.OnBeforePlayAsync(info);

        var deviceId = await GetActiveDeviceIdAsync(accessToken);
        var payload = new { uris = new[] { info.TrackUri }, position_ms = 0 };
        Func<Task>? onRetry = _trackRecorder != null
            ? () => _trackRecorder.OnBeforePlayAsync(info)
            : null;
        return await PutAsync(accessToken, PlayUrl(deviceId), payload, onRetry);
    }

    private static string PlayUrl(string? deviceId) =>
        deviceId == null
            ? "https://api.spotify.com/v1/me/player/play"
            : $"https://api.spotify.com/v1/me/player/play?device_id={deviceId}";

    public async Task<bool> PauseAsync(string accessToken) =>
        await PutAsync(accessToken,
            "https://api.spotify.com/v1/me/player/pause", null);

    public async Task<bool> ResumeAsync(string accessToken) =>
        await PutAsync(accessToken,
            "https://api.spotify.com/v1/me/player/play", null);

    public async Task<bool> NextTrackAsync(string accessToken) =>
        await PostAsync(accessToken,
            "https://api.spotify.com/v1/me/player/next");

    public async Task<bool> PreviousTrackAsync(string accessToken) =>
        await PostAsync(accessToken,
            "https://api.spotify.com/v1/me/player/previous");

    public async Task<bool> SetVolumeAsync(string accessToken, int volumePercent)
    {
        var url = $"https://api.spotify.com/v1/me/player/volume" +
                  $"?volume_percent={volumePercent}";
        return await PutAsync(accessToken, url, null);
    }

    private async Task<JsonDocument?> GetJsonAsync(string accessToken, string url)
    {
        using var response = await GetResponseAsync(accessToken, url);

        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("Spotify GET {Url} → {Status}: {Body}",
                url, (int)response.StatusCode, content);
            return null;
        }

        if (string.IsNullOrWhiteSpace(content)) return null;

        return JsonDocument.Parse(content);
    }

    private async Task<HttpResponseMessage> GetResponseAsync(string accessToken, string url)
    {
        var response = await SendGetAsync(accessToken, url);

        if ((int)response.StatusCode == 429)
        {
            await WaitForRetryAsync(response);
            response.Dispose();
            response = await SendGetAsync(accessToken, url);
        }

        if ((int)response.StatusCode == 401)
        {
            var newToken = await _tokenRefresher.RefreshAsync();
            if (!string.IsNullOrEmpty(newToken))
            {
                response.Dispose();
                response = await SendGetAsync(newToken, url);
            }
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendGetAsync(string accessToken, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return await _http.SendAsync(request);
    }

    private async Task<bool> PutAsync(string accessToken, string url, object? body,
        Func<Task>? onBeforeRetry = null)
    {
        var response = await SendPutAsync(accessToken, url, body);

        if ((int)response.StatusCode == 429)
        {
            await WaitForRetryAsync(response);
            if (onBeforeRetry != null) await onBeforeRetry();
            response = await SendPutAsync(accessToken, url, body);
        }

        if ((int)response.StatusCode == 401)
        {
            var newToken = await _tokenRefresher.RefreshAsync();
            if (newToken != null)
                response = await SendPutAsync(newToken, url, body);
        }

        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 204)
            _log.LogError("Spotify PUT {Url} → {Status}: {Body}",
                url, (int)response.StatusCode, await response.Content.ReadAsStringAsync());

        return response.IsSuccessStatusCode || (int)response.StatusCode == 204;
    }

    private async Task<HttpResponseMessage> SendPutAsync(string accessToken, string url, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body != null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _http.SendAsync(request);
    }

    private async Task<bool> PostAsync(string accessToken, string url)
    {
        var response = await SendPostAsync(accessToken, url);

        if ((int)response.StatusCode == 429)
        {
            await WaitForRetryAsync(response);
            response = await SendPostAsync(accessToken, url);
        }

        if ((int)response.StatusCode == 401)
        {
            var newToken = await _tokenRefresher.RefreshAsync();
            if (newToken != null)
                response = await SendPostAsync(newToken, url);
        }

        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 204)
            _log.LogError("Spotify POST {Url} → {Status}: {Body}",
                url, (int)response.StatusCode, await response.Content.ReadAsStringAsync());

        return response.IsSuccessStatusCode || (int)response.StatusCode == 204;
    }

    private async Task<HttpResponseMessage> SendPostAsync(string accessToken, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("", Encoding.UTF8, "application/json");
        return await _http.SendAsync(request);
    }

    private async Task<bool> PostWithBodyAsync(string accessToken, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request);

        if ((int)response.StatusCode == 429)
        {
            await WaitForRetryAsync(response);
            request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            response = await _http.SendAsync(request);
        }

        if (!response.IsSuccessStatusCode)
            _log.LogError("Spotify POST {Url} → {Status}: {Body}",
                url, (int)response.StatusCode, await response.Content.ReadAsStringAsync());

        return response.IsSuccessStatusCode;
    }

    private async Task WaitForRetryAsync(HttpResponseMessage response)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5;
        var ts = TimeSpan.FromSeconds(seconds);
        _log.LogWarning("Spotify rate limit hit — retrying in {Time}",
            $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException) { }
    }

    private static SpotifyPlaylist ParsePlaylist(JsonElement e)
    {
        string? imageUrl = null;
        if (e.TryGetProperty("images", out var imgs) &&
            imgs.ValueKind == JsonValueKind.Array &&
            imgs.GetArrayLength() > 0)
        {
            imageUrl = imgs[0].TryGetProperty("url", out var u)
                ? u.GetString() : null;
        }

        return new SpotifyPlaylist
        {
            Id = e.GetProperty("id").GetString()!,
            Name = e.GetProperty("name").GetString()!,
            Description = e.TryGetProperty("description", out var d)
                ? d.GetString() : null,
            Uri = e.GetProperty("uri").GetString()!,
            TrackCount = e.TryGetProperty("items", out var t) &&
                         t.ValueKind == JsonValueKind.Object &&
                         t.TryGetProperty("total", out var tot)
                ? tot.GetInt32() : 0,
            ImageUrl = imageUrl
        };
    }

    private static SpotifyTrack ParseTrack(JsonElement e)
    {
        var artists = e.TryGetProperty("artists", out var arr)
            ? string.Join(", ", arr.EnumerateArray()
                .Select(a => a.GetProperty("name").GetString()))
            : "";

        string? albumName = null;
        string? imageUrl = null;
        if (e.TryGetProperty("album", out var album))
        {
            albumName = album.TryGetProperty("name", out var an)
                ? an.GetString() : null;
            if (album.TryGetProperty("images", out var imgs) &&
                imgs.GetArrayLength() > 0)
            {
                imageUrl = imgs[0].TryGetProperty("url", out var u)
                    ? u.GetString() : null;
            }
        }

        return new SpotifyTrack
        {
            Id = e.TryGetProperty("id", out var id) ? id.GetString()! : "",
            Name = e.GetProperty("name").GetString()!,
            Artists = artists,
            Album = albumName ?? "",
            Uri = e.TryGetProperty("uri", out var uri) ? uri.GetString()! : "",
            DurationMs = e.TryGetProperty("duration_ms", out var dur)
                ? dur.GetInt32() : 0,
            TrackNumber = e.TryGetProperty("track_number", out var tn)
                ? tn.GetInt32() : 0,
            ImageUrl = imageUrl
        };
    }
}
