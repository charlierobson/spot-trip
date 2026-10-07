namespace SpotifyRemote.Models;

public class TokenData
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}

public class SpotifyPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int TrackCount { get; set; }
    public string? ImageUrl { get; set; }
    public string Uri { get; set; } = "";
}

public class SpotifyTrack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Artists { get; set; } = "";
    public string Album { get; set; } = "";
    public int DurationMs { get; set; }
    public string Uri { get; set; } = "";
    public string? ImageUrl { get; set; }
    public int TrackNumber { get; set; }

    public string DurationFormatted =>
        $"{DurationMs / 60000}:{(DurationMs % 60000 / 1000):D2}";
}

public class PlaybackState
{
    public bool IsPlaying { get; set; }
    public SpotifyTrack? CurrentTrack { get; set; }
    public int ProgressMs { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public int VolumePercent { get; set; }

    public string ProgressFormatted =>
        $"{ProgressMs / 60000}:{(ProgressMs % 60000 / 1000):D2}";
}

public sealed record PlaybackStateResult(bool IsAvailable, PlaybackState? Playback);

public class TrackPlaybackInfo
{
    public string TrackId { get; set; } = "";
    public string TrackUri { get; set; } = "";
    public string TrackName { get; set; } = "";
    public string Artists { get; set; } = "";
    public string Album { get; set; } = "";
    public int DurationMs { get; set; }
    public int TrackNumber { get; set; }
    public string? ImageUrl { get; set; }
    public string PlaylistUri { get; set; } = "";
    public string PlaylistId { get; set; } = "";
    public string PlaylistName { get; set; } = "";
    public string FileExtension { get; set; } = "";
    public bool SkipIfExists { get; set; }
}

public class ManagedQueue
{
    public List<TrackPlaybackInfo> Tracks { get; set; } = new();
    public int CurrentIndex { get; set; }

    public TrackPlaybackInfo? Current => CurrentIndex < Tracks.Count ? Tracks[CurrentIndex] : null;
    public bool IsComplete => CurrentIndex >= Tracks.Count;
    public int Total => Tracks.Count;
}

public class PlayerViewModel
{
    public List<SpotifyPlaylist> Playlists { get; set; } = new();
    public PlaybackState? Playback { get; set; }
    public string? ActivePlaylistId { get; set; }
    public List<SpotifyTrack> PlaylistTracks { get; set; } = new();
    public ManagedQueue? Queue { get; set; }
}
