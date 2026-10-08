namespace SpotifyRemote.Services;

public enum AudioFileFormat
{
    Wav,
    Flac
}

// Placeholder: expected to grow as the recorders gain settings.
public sealed record RecorderOptions
{
    public string AudioDevice { get; init; } = "";
    public AudioFileFormat FileFormat { get; init; } = AudioFileFormat.Flac;
    public bool SkipExistingFiles { get; init; }
}
