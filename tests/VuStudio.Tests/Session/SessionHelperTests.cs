using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;
using VuStudio.Session;
using VuStudio.Session.Logging;
using VuStudio.Session.Media;
using VuStudio.Session.Service;
using VuStudio.Session.Signals;

namespace VuStudio.Tests.Session;

public class MediaTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Extrapolates_while_playing()
    {
        var p = MediaTimeline.Compute(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(50), T0, playing: true, 1.0, T0.AddSeconds(10));
        Assert.Equal(new MediaPosition(60, 200, 30), p);
    }

    [Fact]
    public void Honours_the_playback_rate_and_does_not_move_when_paused()
    {
        Assert.Equal(70, MediaTimeline.Compute(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(50), T0, true, 2.0, T0.AddSeconds(10)).PositionSeconds);
        Assert.Equal(50, MediaTimeline.Compute(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(50), T0, false, 1.0, T0.AddSeconds(10)).PositionSeconds);
    }

    [Fact]
    public void Never_updated_timelines_are_not_extrapolated() =>
        Assert.Equal(50, MediaTimeline.Compute(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(50), default, true, 1.0, T0).PositionSeconds);

    [Fact]
    public void Clamps_to_the_track_and_handles_missing_duration()
    {
        Assert.Equal(new MediaPosition(200, 200, 100), MediaTimeline.Compute(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(190), T0, true, null, T0.AddMinutes(5)));
        Assert.Equal(new MediaPosition(12, null, null), MediaTimeline.Compute(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(12), T0, false, null, T0));
        Assert.Equal(new MediaPosition(5, 10, 50), MediaTimeline.Compute(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(105), T0, false, null, T0));
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "ZuneMusic")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "vlc")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("308046B0AF4A39CB", "308046B0AF4A39CB")]
    [InlineData("  ", null)]
    public void Friendly_app_names(string aumid, string? expected) => Assert.Equal(expected, MediaAppName.Friendly(aumid));
}

public class SystemSignalTests
{
    [Theory]
    [InlineData(10_000u, 4_000u, 6_000)]
    [InlineData(500u, 0xFFFF_FE0Cu, 1_000)] // tick wrapped (49.7 days)
    [InlineData(4_000u, 10_000u, 0)]        // last input "in the future": not incremental
    [InlineData(7u, 7u, 0)]
    public void Idle_time_from_ticks(uint now, uint last, double expectedMs) =>
        Assert.Equal(expectedMs, IdleTime.FromTicks(now, last).TotalMilliseconds);

    [Theory]
    [InlineData(@"C:\Games\EldenRing.exe", "eldenring.exe")]
    [InlineData("Spotify", "spotify.exe")]
    [InlineData("code.EXE", "code.exe")]
    [InlineData("", null)]
    [InlineData(@"C:\dir\", null)]
    public void Exe_names(string input, string? expected) => Assert.Equal(expected, ProcessNames.ToExeName(input));

    [Fact]
    public void Accent_encodings()
    {
        Assert.Equal("#0078D7", AccentColors.FromArgb(0xC40078D7));
        Assert.Equal("#0078D7", AccentColors.FromAbgr(0xFFD77800));
        Assert.Equal("#0A0B0C", AccentColors.FromRgb(10, 11, 12));
    }

    [Fact]
    public void Fullscreen_detection()
    {
        var monitor = new ScreenRect(0, 0, 2560, 1440);
        Assert.True(FullscreenDetector.IsFullscreen(FullscreenDetector.QunsRunningD3DFullScreen, null, null, false));
        Assert.True(FullscreenDetector.IsFullscreen(FullscreenDetector.QunsBusy, null, null, false));
        Assert.True(FullscreenDetector.IsFullscreen(5, new ScreenRect(0, 0, 2560, 1440), monitor, false));
        Assert.True(FullscreenDetector.IsFullscreen(5, new ScreenRect(-8, -8, 2568, 1448), monitor, false)); // maximised-borderless overhang
        Assert.False(FullscreenDetector.IsFullscreen(5, new ScreenRect(0, 0, 2560, 1400), monitor, false));
        Assert.False(FullscreenDetector.IsFullscreen(5, new ScreenRect(0, 0, 2560, 1440), monitor, isShellWindow: true));
        Assert.False(FullscreenDetector.IsFullscreen(null, null, monitor, false));
    }

    [Fact]
    public void Peak_window_keeps_the_max_of_recent_samples()
    {
        var w = new PeakWindow(TimeSpan.FromMilliseconds(75));
        w.Add(TimeSpan.FromMilliseconds(0), 0.9);
        w.Add(TimeSpan.FromMilliseconds(33), 0.1);
        w.Add(TimeSpan.FromMilliseconds(66), 0.2);
        Assert.Equal(0.9, w.Max(TimeSpan.FromMilliseconds(66)));
        Assert.Equal(0.2, w.Max(TimeSpan.FromMilliseconds(100)));
        Assert.Null(w.Max(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Lock_events_win_over_a_lagging_poll()
    {
        var a = new LockStateArbiter(TimeSpan.FromSeconds(3));
        Assert.True(a.OnPoll(true, TimeSpan.FromSeconds(1)));      // started while locked
        a.OnEvent(false, TimeSpan.FromSeconds(10));                // unlocked
        Assert.False(a.OnPoll(true, TimeSpan.FromSeconds(11)));    // stale poll ignored
        Assert.True(a.OnPoll(true, TimeSpan.FromSeconds(14)));     // persistent disagreement wins
        Assert.True(a.OnPoll(null, TimeSpan.FromSeconds(20)));     // unknown keeps the state
    }
}

public class SessionCatalogTests
{
    [Fact]
    public void Ids_match_the_well_known_engine_sensors()
    {
        string Full(string local) => $"{SessionSensors.Provider}/{local}";
        Assert.Equal(WellKnownSensors.SessionLocked, Full(SessionSensors.Locked));
        Assert.Equal(WellKnownSensors.SessionIdleSeconds, Full(SessionSensors.Idle));
        Assert.Equal(WellKnownSensors.ForegroundProcess, Full(SessionSensors.ForegroundProcess));
        Assert.Equal(WellKnownSensors.AccentColor, Full(SessionSensors.Accent));
        Assert.Equal(WellKnownSensors.AlbumColor, Full(SessionSensors.MediaColor));
        Assert.Equal(WellKnownSensors.MediaTitle, Full(SessionSensors.MediaTitle));
        Assert.Equal(WellKnownSensors.MediaArtist, Full(SessionSensors.MediaArtist));
        Assert.Equal(WellKnownSensors.MediaPlaying, Full(SessionSensors.MediaPlaying));
        Assert.Equal(WellKnownSensors.MediaProgress, Full(SessionSensors.MediaProgress));
        Assert.Equal(WellKnownSensors.AudioPeak, Full(SessionSensors.AudioPeak));
        Assert.Equal(WellKnownSensors.AudioPeakLeft, Full(SessionSensors.AudioPeakLeft));
        Assert.Equal(WellKnownSensors.AudioPeakRight, Full(SessionSensors.AudioPeakRight));
        Assert.Equal(WellKnownSensors.AudioVolume, Full(SessionSensors.AudioVolume));
    }

    [Fact]
    public void Every_sensor_has_full_metadata_and_a_bundled_icon()
    {
        var icons = JsonDocument.Parse(File.ReadAllText(FindRepoFile("src/VuStudio.Rendering/Assets/Icons/icons.json")))
            .RootElement.EnumerateObject().SelectMany(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out _))
            .Select(e => e.GetProperty("id").GetString())
            .ToHashSet();
        var all = SessionSensors.All.ToList();
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
        Assert.DoesNotContain(all, s => s.Id == SessionSensors.Locked); // owned by the service
        foreach (var s in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.NotNull(s.Kind);
            Assert.Contains(s.Icon, icons);
            Assert.True(s.StaleAfterSeconds > 0);
            Assert.Contains(s.Category, new[] { SensorCategory.System, SensorCategory.Audio, SensorCategory.Media, SensorCategory.Game });
            if (s.ValueKind == SensorValueKind.Number)
            {
                Assert.NotNull(s.Min);
            }
        }
    }

    [Fact]
    public void Tray_strings_exist_in_both_languages()
    {
        foreach (var key in TrayText.Keys)
        {
            var (en, it) = TrayText.Raw(key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(it), key);
            Assert.Equal(en.Count(c => c == '{'), it.Count(c => c == '{'));
        }

        Assert.Equal("Esci", new TrayText(new CultureInfo("it-IT"))["Exit"]);
        Assert.Equal("Exit", new TrayText(new CultureInfo("de-DE"))["Exit"]);
        Assert.Equal("Automatic (Gaming)", new TrayText(CultureInfo.InvariantCulture).Format("AutomaticActive", "Gaming"));
    }

    internal static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }
}

public sealed class ServiceLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vustudio-tray-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("""{ "web": { "port": 6000 } }""", 6000)]
    [InlineData("{ // comment\n \"Web\": { \"Port\": \"6001\", }, }", 6001)]
    [InlineData("""{ "web": { "port": 0 } }""", null)]
    [InlineData("""{ "web": { } }""", null)]
    [InlineData("""not json""", null)]
    public void Reads_web_port(string json, int? expected) => Assert.Equal(expected, ServiceLocator.ReadPort(json));

    [Fact]
    public void Port_resolution_order()
    {
        var config = Path.Combine(_dir, "config.json");
        File.WriteAllText(config, """{ "web": { "port": 7000 } }""");
        static string? NoEnv(string _) => null;
        static string? Env(string name) => name == "VUSTUDIO_PORT" ? "7100" : null;

        Assert.Equal(7200, ServiceLocator.ResolvePort(7200, Env, config));
        Assert.Equal(7100, ServiceLocator.ResolvePort(null, Env, config));
        Assert.Equal(7000, ServiceLocator.ResolvePort(null, NoEnv, config));
        Assert.Equal(5341, ServiceLocator.ResolvePort(null, NoEnv, Path.Combine(_dir, "missing.json")));
        Assert.Equal(7000, ServiceLocator.ResolvePort(null, n => n == "VUSTUDIO_DATA" ? _dir : null));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5341/sensors?x=1", true)]
    [InlineData("http://localhost:5341/", true)]
    [InlineData("http://[::1]:5341/", true)]
    [InlineData("http://127.0.0.1:5342/", false)]
    [InlineData("https://127.0.0.1:5341/", false)]
    [InlineData("http://evil.example:5341/", false)]
    [InlineData("http://127.0.0.1.nip.io:5341/", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    public void Navigation_is_restricted_to_the_local_service(string url, bool allowed) =>
        Assert.Equal(allowed, NavigationPolicy.IsAppUri(new Uri(url), ServiceLocator.AppUri(5341)));

    [Theory]
    [InlineData("https://github.com/blinkone/vudials", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:display", false)]
    public void Only_web_links_open_externally(string url, bool allowed) =>
        Assert.Equal(allowed, NavigationPolicy.CanOpenExternally(new Uri(url)));
}

public class VuStudioApiClientTests
{
    [Fact]
    public async Task Reads_info_state_and_profiles_and_posts_with_the_csrf_header()
    {
        var posts = new List<(string Path, string? Csrf, bool HasOrigin, string Body)>();
        await using var host = await TestHost.StartAsync(app =>
        {
            app.MapGet("/api/info", () => Results.Json(new { name = "VU Studio", version = "1.2.3", machine = "PC" }));
            app.MapGet("/api/state", () => Results.Json(new
            {
                revision = 12,
                engine = new { activeProfileId = "gaming", activeProfileName = "Gaming", manualProfileId = (string?)null, paused = true },
            }));
            app.MapGet("/api/config", () => Results.Json(new
            {
                profiles = new object[] { new { id = "default", name = "Default", enabled = true }, new { id = "gaming", name = "Gaming", enabled = false } },
            }));
            app.MapPost("/api/{**path}", async (HttpContext http) =>
            {
                using var reader = new StreamReader(http.Request.Body);
                var body = await reader.ReadToEndAsync();
                lock (posts)
                {
                    posts.Add((http.Request.Path, http.Request.Headers["X-VuStudio"].ToString(), http.Request.Headers.ContainsKey("Origin"), body));
                }

                return Results.NoContent();
            });
        });

        using var api = new VuStudioApiClient(host.BaseUri);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(new ServiceInfo("VU Studio", "1.2.3", "PC"), await api.TryGetInfoAsync(ct));
        Assert.Equal(new EngineStatus(12, "gaming", "Gaming", null, true), await api.GetEngineStatusAsync(ct));
        Assert.Equal([new ProfileSummary("default", "Default", true), new ProfileSummary("gaming", "Gaming", false)], await api.GetProfilesAsync(ct));

        await api.SetProfileAsync("gaming", ct);
        await api.SetProfileAsync(null, ct);
        await api.SetPausedAsync(false, ct);
        await api.RefreshDialsAsync(ct);

        Assert.Equal(["/api/engine/profile", "/api/engine/profile", "/api/engine/pause", "/api/engine/refresh"], posts.Select(p => p.Path));
        Assert.All(posts, p => Assert.Equal("1", p.Csrf));
        Assert.All(posts, p => Assert.False(p.HasOrigin));
        Assert.Equal("""{"profileId":"gaming"}""", posts[0].Body);
        Assert.Equal("""{"profileId":null}""", posts[1].Body);
        Assert.Equal("""{"paused":false}""", posts[2].Body);
    }

    [Fact]
    public async Task Unreachable_service_gives_null_info()
    {
        using var api = new VuStudioApiClient(new Uri("http://127.0.0.1:1/"), timeout: TimeSpan.FromSeconds(2));
        Assert.Null(await api.TryGetInfoAsync(TestContext.Current.CancellationToken));
    }
}

public sealed class FileLoggerFactoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vustudio-logs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Writes_daily_files_and_keeps_seven()
    {
        for (var day = 1; day <= 9; day++)
        {
            File.WriteAllText(Path.Combine(_dir, $"tray-202609{day:00}.log"), "old");
        }

        var now = new DateTime(2026, 10, 12, 8, 30, 0);
        var factory = new FileLoggerFactory(_dir, "tray", LogLevel.Information, () => now);
        var log = factory.CreateLogger("VuStudio.Tray.Audio");
        log.LogDebug("hidden");
        log.LogInformation("Device {Name}", "Speakers");
        log.LogError(new InvalidOperationException("boom"), "Failed");
        factory.Dispose();

        var text = File.ReadAllText(Path.Combine(_dir, "tray-20261012.log"));
        Assert.Contains("INF Audio: Device Speakers", text);
        Assert.Contains("ERR Audio: Failed", text);
        Assert.Contains("boom", text);
        Assert.DoesNotContain("hidden", text);
        Assert.Equal(7, Directory.GetFiles(_dir, "tray-*.log").Length);
    }
}
