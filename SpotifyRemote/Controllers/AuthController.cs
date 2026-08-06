using Microsoft.AspNetCore.Mvc;
using SpotifyRemote.Services;

namespace SpotifyRemote.Controllers;

public class AuthController : Controller
{
    private readonly SpotifyAuthService _auth;

    public AuthController(SpotifyAuthService auth)
    {
        _auth = auth;
    }

    [HttpGet("/auth/login")]
    public IActionResult Login()
    {
        var state = Guid.NewGuid().ToString("N");
        HttpContext.Session.SetString("oauth_state", state);
        return Redirect(_auth.BuildAuthorizationUrl(state));
    }

    [HttpGet("/auth/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        if (!string.IsNullOrEmpty(error))
            return RedirectToAction("Index", "Home",
                new { error = "Spotify authorization denied." });

        var savedState = HttpContext.Session.GetString("oauth_state");
        if (state != savedState)
            return RedirectToAction("Index", "Home",
                new { error = "Invalid OAuth state." });

        if (string.IsNullOrEmpty(code))
            return RedirectToAction("Index", "Home",
                new { error = "No authorization code received." });

        var tokens = await _auth.ExchangeCodeAsync(code);
        if (tokens == null)
            return RedirectToAction("Index", "Home",
                new { error = "Failed to exchange authorization code." });

        HttpContext.Session.SetString("access_token", tokens.AccessToken);
        HttpContext.Session.SetString("refresh_token", tokens.RefreshToken);
        HttpContext.Session.SetString("expires_at",
            tokens.ExpiresAt.ToString("O"));

        return RedirectToAction("Index", "Player");
    }

    [HttpGet("/auth/logout")]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Index", "Home");
    }
}
