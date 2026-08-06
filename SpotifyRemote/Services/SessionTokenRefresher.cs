namespace SpotifyRemote.Services;

public class SessionTokenRefresher : ITokenRefresher
{
    private readonly IHttpContextAccessor _accessor;
    private readonly SpotifyAuthService _auth;

    public SessionTokenRefresher(IHttpContextAccessor accessor, SpotifyAuthService auth)
    {
        _accessor = accessor;
        _auth = auth;
    }

    public async Task<string?> RefreshAsync()
    {
        ISession? session = _accessor.HttpContext?.Session;
        if (session is null) return null;

        var refreshToken = session.GetString("refresh_token");
        if (string.IsNullOrEmpty(refreshToken)) return null;

        var tokens = await _auth.RefreshTokenAsync(refreshToken);
        if (tokens == null) return null;

        session.SetString("access_token", tokens.AccessToken);
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            session.SetString("refresh_token", tokens.RefreshToken);
        session.SetString("expires_at", tokens.ExpiresAt.ToString("O"));

        return tokens.AccessToken;
    }
}
