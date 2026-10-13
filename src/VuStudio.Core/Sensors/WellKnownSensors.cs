namespace VuStudio.Core.Sensors;

/// <summary>
/// Ids of sensors with special meaning for the engine. The <c>session/…</c> sensors are published
/// by the tray app (user-session agent) through the session channel.
/// </summary>
public static class WellKnownSensors
{
    public const string SessionProvider = "session";

    /// <summary>1 while the workstation is locked, else 0.</summary>
    public const string SessionLocked = "session/locked";

    /// <summary>Seconds since the last user input.</summary>
    public const string SessionIdleSeconds = "session/idle";

    /// <summary>Executable name of the foreground window's process (text, e.g. "eldenring.exe").</summary>
    public const string ForegroundProcess = "session/foreground/process";

    /// <summary>Windows accent color (#RRGGBB).</summary>
    public const string AccentColor = "session/accent";

    /// <summary>Dominant color of the current album art (#RRGGBB).</summary>
    public const string AlbumColor = "session/media/color";

    public const string MediaTitle = "session/media/title";

    public const string MediaArtist = "session/media/artist";

    /// <summary>1 while media is playing.</summary>
    public const string MediaPlaying = "session/media/playing";

    /// <summary>Playback progress 0..100.</summary>
    public const string MediaProgress = "session/media/progress";

    /// <summary>Output peak level 0..100 (left, right, max).</summary>
    public const string AudioPeak = "session/audio/peak";

    public const string AudioPeakLeft = "session/audio/peak-left";

    public const string AudioPeakRight = "session/audio/peak-right";

    /// <summary>Master volume 0..100.</summary>
    public const string AudioVolume = "session/audio/volume";
}
