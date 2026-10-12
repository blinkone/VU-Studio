using VuStudio.Core.Sensors;

namespace VuStudio.Session;

/// <summary>
/// Local ids and metadata of the <c>session/*</c> sensors the tray publishes. Full ids are
/// <c>session/{local id}</c>; those the engine relies on match <see cref="WellKnownSensors"/>.
/// </summary>
public static class SessionSensors
{
    public const string Provider = WellKnownSensors.SessionProvider;

    // System
    /// <summary>Reported only: the service owns its definition (<c>SessionState</c>) and merges it with its own session events.</summary>
    public const string Locked = "locked";
    public const string Idle = "idle";
    public const string ForegroundProcess = "foreground/process";
    public const string ForegroundFullscreen = "foreground/fullscreen";
    public const string Accent = "accent";

    // Audio
    public const string AudioPeak = "audio/peak";
    public const string AudioPeakLeft = "audio/peak-left";
    public const string AudioPeakRight = "audio/peak-right";
    public const string AudioVolume = "audio/volume";
    public const string AudioMute = "audio/mute";
    public const string Mic = "audio/mic";
    public const string MicMute = "audio/mic-mute";

    // Media
    public const string MediaTitle = "media/title";
    public const string MediaArtist = "media/artist";
    public const string MediaAlbum = "media/album";
    public const string MediaApp = "media/app";
    public const string MediaPlaying = "media/playing";
    public const string MediaPosition = "media/position";
    public const string MediaDuration = "media/duration";
    public const string MediaProgress = "media/progress";
    public const string MediaColor = "media/color";

    // Game (RTSS)
    public const string GameFps = "game/fps";
    public const string GameFrameTime = "game/frametime";
    public const string GameApp = "game/app";

    public static IReadOnlyList<SensorInfo> System { get; } =
    [
        Number(Idle, "Idle time", SensorCategory.System, SensorKinds.Duration, Units.Seconds, 0, 3600, "timer-sand", 10),
        Text(ForegroundProcess, "Foreground app", SensorCategory.System, "monitor", 10),
        Number(ForegroundFullscreen, "Foreground app full screen", SensorCategory.System, SensorKinds.State, Units.None, 0, 1, "monitor-dashboard", 10),
        Color(Accent, "Windows accent color", SensorCategory.System, "microsoft-windows", 60),
    ];

    public static IReadOnlyList<SensorInfo> Audio { get; } =
    [
        Number(AudioPeak, "Output peak", SensorCategory.Audio, SensorKinds.Level, Units.Percent, 0, 100, "waveform", 5),
        Number(AudioPeakLeft, "Output peak left", SensorCategory.Audio, SensorKinds.Level, Units.Percent, 0, 100, "waveform", 5),
        Number(AudioPeakRight, "Output peak right", SensorCategory.Audio, SensorKinds.Level, Units.Percent, 0, 100, "waveform", 5),
        Number(AudioVolume, "Master volume", SensorCategory.Audio, SensorKinds.Level, Units.Percent, 0, 100, "volume-high", 10),
        Number(AudioMute, "Output muted", SensorCategory.Audio, SensorKinds.State, Units.None, 0, 1, "volume-off", 10),
        Number(Mic, "Microphone peak", SensorCategory.Audio, SensorKinds.Level, Units.Percent, 0, 100, "microphone", 5),
        Number(MicMute, "Microphone muted", SensorCategory.Audio, SensorKinds.State, Units.None, 0, 1, "microphone-off", 10),
    ];

    public static IReadOnlyList<SensorInfo> Media { get; } =
    [
        Text(MediaTitle, "Media title", SensorCategory.Media, "music-note", 30),
        Text(MediaArtist, "Media artist", SensorCategory.Media, "account", 30),
        Text(MediaAlbum, "Media album", SensorCategory.Media, "album", 30),
        Text(MediaApp, "Media player", SensorCategory.Media, "play", 30),
        Number(MediaPlaying, "Media playing", SensorCategory.Media, SensorKinds.State, Units.None, 0, 1, "play", 10),
        Number(MediaPosition, "Media position", SensorCategory.Media, SensorKinds.Duration, Units.Seconds, 0, null, "timer-outline", 10),
        Number(MediaDuration, "Media duration", SensorCategory.Media, SensorKinds.Duration, Units.Seconds, 0, null, "av-timer", 30),
        Number(MediaProgress, "Media progress", SensorCategory.Media, SensorKinds.Percent, Units.Percent, 0, 100, "chart-timeline-variant", 10),
        Color(MediaColor, "Album art color", SensorCategory.Media, "album", 60),
    ];

    public static IReadOnlyList<SensorInfo> Game { get; } =
    [
        Number(GameFps, "Game FPS", SensorCategory.Game, SensorKinds.Fps, Units.Fps, 0, 240, "gamepad-variant", 5),
        Number(GameFrameTime, "Game frame time", SensorCategory.Game, SensorKinds.FrameTime, Units.Milliseconds, 0, 50, "pulse", 5),
        Text(GameApp, "Game", SensorCategory.Game, "controller", 5),
    ];

    public static IEnumerable<SensorInfo> All => System.Concat(Audio).Concat(Media).Concat(Game);

    private static SensorInfo Number(string id, string name, SensorCategory category, string kind, string unit, double? min, double? max, string icon, double staleAfter) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Kind = kind,
        Unit = unit,
        ValueKind = SensorValueKind.Number,
        Min = min,
        Max = max,
        Icon = icon,
        StaleAfterSeconds = staleAfter,
    };

    private static SensorInfo Text(string id, string name, SensorCategory category, string icon, double staleAfter) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Kind = SensorKinds.Text,
        ValueKind = SensorValueKind.Text,
        Icon = icon,
        StaleAfterSeconds = staleAfter,
    };

    private static SensorInfo Color(string id, string name, SensorCategory category, string icon, double staleAfter) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Kind = SensorKinds.Color,
        ValueKind = SensorValueKind.Color,
        Icon = icon,
        StaleAfterSeconds = staleAfter,
    };
}
