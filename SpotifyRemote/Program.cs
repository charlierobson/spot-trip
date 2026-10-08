using System.Diagnostics;
using Serilog;
using Serilog.Events;
using SpotifyRemote.Services;

var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Library", "Logs", "SpotifyRemote");
Directory.CreateDirectory(logDir);

var recorderLog = new RecorderLogBroadcaster();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning)
    .WriteTo.File(
        Path.Combine(logDir, "spotifyremote-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true)
        .WriteTo.Sink(new ActionSink(logEvent => {
            processLogMessage(logEvent);
        }))
    .CreateLogger();

void processLogMessage(LogEvent logEvent)
{
    // Recorder state log entries use a "Line" property for the SSE window.
    // a "Line" property — that's a more reliable filter than matching text,
    // since the message template itself never contains the substituted values.
    if (logEvent.Properties.TryGetValue("Line", out var lineValue) &&
        lineValue is ScalarValue { Value: string line })
    {
        recorderLog.Publish(line);
    }
}

var builder = WebApplication.CreateBuilder(args);

// Optional, gitignored override for secrets (e.g. Spotify:ClientSecret) —
// a plain JSON file next to the exe, edited by hand, no dev tooling required.
// Values here win over appsettings.json. See appsettings.Local.json.example.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Host.UseSerilog();
builder.Services.AddSingleton(recorderLog);
builder.Services.AddSingleton<IRecorderLogPublisher>(recorderLog);

builder.Services.AddControllersWithViews();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddHttpClient("spotify-accounts");
builder.Services.AddHttpClient("spotify-api");

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<SpotifyAuthService>();
builder.Services.AddScoped<ITokenRefresher, SessionTokenRefresher>();
builder.Services.AddScoped<SpotifyApiService>();
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<ISleepInhibitor, WinSleepInhibitor>();
    builder.Services.AddSingleton<ITrackRecorder, WinTrackRecorder>();
}
else
{
    builder.Services.AddSingleton<ISleepInhibitor, MacSleepInhibitor>();
    builder.Services.AddSingleton<ITrackRecorder, MacTrackRecorder>();
}
builder.Services.AddSingleton<RecorderStatusReporter>();

var app = builder.Build();

_ = app.Services.GetRequiredService<RecorderStatusReporter>();
CheckPreflight(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Server-sent event stream of recorder state and progress logs.
app.MapGet("/logs/recorder/stream", async (HttpContext ctx, RecorderLogBroadcaster broadcaster, CancellationToken token) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    using var subscription = broadcaster.Subscribe(out var reader);
    try
    {
        await foreach (var line in reader.ReadAllAsync(token))
        {
            var safe = line.Replace("\r", "").Replace("\n", " ");
            await ctx.Response.WriteAsync($"data: {safe}\n\n", token);
            await ctx.Response.Body.FlushAsync(token);
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:6502";
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    log.LogInformation("SpotifyRemote listening at {Url}", url);
    OpenBrowser(url);
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    // Runs on Ctrl-C / SIGTERM (graceful shutdown only — can't run at all on
    // a hard kill -9). Stop any in-flight recorder so its file gets flushed
    // instead of orphaned, and close open log streams so Kestrel isn't stuck
    // waiting on them for the rest of the shutdown timeout.
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    log.LogInformation("Shutting down — stopping any in-flight recorder and closing log streams.");

    recorderLog.CompleteAll();
    app.Services.GetService<ITrackRecorder>()?.OnAbort();
});

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else
            Process.Start("xdg-open", url);
    }
    catch { }
}

static void CheckPreflight(WebApplication app)
{
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    var recorder = app.Services.GetRequiredService<ITrackRecorder>();

    if (!recorder.IsAvailable)
    {
        log.LogError("PRE-FLIGHT FAILED: recorder is unavailable. " +
                     "Place the recorder executable in Tools/recorder before publishing.");
        return;
    }

    log.LogInformation("PRE-FLIGHT OK: recorder is available.");

    var devices = recorder.GetAvailableDevices();
    log.LogInformation("Recording devices ({Count}): {Devices}",
        devices.Count, string.Join(", ", devices));
    if (devices.Count > 0 && !devices.Any(d =>
            d.Contains(recorder.Options.AudioDevice, StringComparison.OrdinalIgnoreCase)))
        log.LogWarning("Configured recording device '{Device}' was not found.",
            recorder.Options.AudioDevice);
}

public class ActionSink : Serilog.Core.ILogEventSink
{
    private readonly Action<LogEvent> _action;
    public ActionSink(Action<LogEvent> action) => _action = action;
    public void Emit(LogEvent logEvent) => _action(logEvent);
}

// Fans out recorder log lines to any number of connected SSE clients.
public class RecorderLogBroadcaster : IRecorderLogPublisher
{
    private const int HistoryLimit = 500;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, System.Threading.Channels.Channel<string>> _subscribers = new();
    private readonly Queue<string> _history = new();

    public IDisposable Subscribe(out System.Threading.Channels.ChannelReader<string> reader)
    {
        var id = Guid.NewGuid();
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        lock (_sync)
        {
            foreach (var line in _history)
                channel.Writer.TryWrite(line);
            _subscribers[id] = channel;
        }
        reader = channel.Reader;
        return new Subscription(() =>
        {
            lock (_sync) _subscribers.Remove(id);
        });
    }

    public void Publish(string line)
    {
        lock (_sync)
        {
            _history.Enqueue(line);
            while (_history.Count > HistoryLimit)
                _history.Dequeue();
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryWrite(line);
        }
    }

    // Ends every open SSE connection's enumeration immediately, so Kestrel's
    // graceful shutdown isn't left waiting on them.
    public void CompleteAll()
    {
        lock (_sync)
        {
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    private class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
