namespace SpotifyRemote.Services;

public interface IRecorderLogPublisher
{
    void Publish(string line);
}