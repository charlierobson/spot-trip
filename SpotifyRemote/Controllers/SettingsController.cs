using Microsoft.AspNetCore.Mvc;
using SpotifyRemote.Services;

namespace SpotifyRemote.Controllers;

public class SettingsController(ITrackRecorder recorder) : Controller
{
    [HttpGet("/settings")]
    public IActionResult Index([FromQuery] bool saved = false) => View(BuildModel(saved));

    [HttpPost("/settings")]
    public IActionResult Save(
        [FromForm] string audioDevice,
        [FromForm] AudioFileFormat fileFormat,
        [FromForm] bool skipExistingFiles)
    {
        if (!recorder.GetAvailableDevices().Contains(audioDevice))
            return BadRequest("Unknown recording device.");

        recorder.Configure(recorder.Options with
        {
            AudioDevice = audioDevice,
            FileFormat = fileFormat,
            SkipExistingFiles = skipExistingFiles
        });
        return Redirect("/settings?saved=true");
    }

    private SettingsViewModel BuildModel(bool saved) => new(
        recorder.IsAvailable,
        recorder.GetAvailableDevices(),
        recorder.Options,
        saved);
}

public sealed record SettingsViewModel(
    bool RecorderAvailable,
    IReadOnlyList<string> Devices,
    RecorderOptions Options,
    bool Saved);
