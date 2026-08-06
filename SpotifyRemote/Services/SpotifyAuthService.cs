using System.Text;
using System.Text.Json;
using SpotifyRemote.Models;

namespace SpotifyRemote.Services;

public class SpotifyAuthService
{
    private readonly IConfiguration _config;
    private readonly HttpClient _http;

    private string ClientId => _config["Spotify:ClientId"]!;
    private string ClientSecret => _config["Spotify:ClientSecret"]!;
    private string RedirectUri => _config["Spotify:RedirectUri"]!;

    private const string Scopes =
        "playlist-read-private playlist-read-collaborative " +
        "playlist-modify-public playlist-modify-private " +
        "user-read-playback-state user-modify-playback-state " +
        "user-read-currently-playing";

    public SpotifyAuthService(IConfiguration config, IHttpClientFactory factory)
    {
        _config = config;
        _http = factory.CreateClient("spotify-accounts");
    }

    public string BuildAuthorizationUrl(string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes,
            ["state"] = state
        };
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"https://accounts.spotify.com/authorize?{qs}";
    }

    public async Task<TokenData?> ExchangeCodeAsync(string code)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri
        });
        return await PostTokenAsync(body);
    }

    public async Task<TokenData?> RefreshTokenAsync(string refreshToken)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        return await PostTokenAsync(body);
    }

    private async Task<TokenData?> PostTokenAsync(FormUrlEncodedContent body)
    {
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://accounts.spotify.com/api/token");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        request.Content = body;

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        return new TokenData
        {
            AccessToken = root.GetProperty("access_token").GetString()!,
            RefreshToken = root.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString()!
                : "",
            ExpiresAt = DateTime.UtcNow.AddSeconds(
                root.GetProperty("expires_in").GetInt32() - 300)
        };
    }
}
