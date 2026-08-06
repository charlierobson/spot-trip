namespace SpotifyRemote.Services;

public interface ITokenRefresher
{
    Task<string?> RefreshAsync();
}
