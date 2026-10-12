namespace VuStudio.Session.Signals;

/// <summary>Idle time from <c>GetLastInputInfo</c> (docs/integrations/windows-sources.md §8.1).</summary>
public static class IdleTime
{
    /// <summary>
    /// Both ticks are in the 32-bit <c>GetTickCount</c> domain (wraps after 49.7 days). The last-input tick is
    /// "not guaranteed to be incremental", so a negative difference (seen as a huge unsigned one) counts as 0.
    /// </summary>
    public static TimeSpan FromTicks(uint nowTick, uint lastInputTick)
    {
        var delta = unchecked(nowTick - lastInputTick);
        return delta > int.MaxValue ? TimeSpan.Zero : TimeSpan.FromMilliseconds(delta);
    }
}

/// <summary>Executable names as the engine compares them (lower case, with <c>.exe</c>).</summary>
public static class ProcessNames
{
    /// <summary><c>C:\Games\EldenRing.exe</c> → <c>eldenring.exe</c>; <c>Spotify</c> → <c>spotify.exe</c>.</summary>
    public static string? ToExeName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return null;
        }

        var s = pathOrName.Trim();
        var slash = s.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            s = s[(slash + 1)..];
        }

        if (s.Length == 0)
        {
            return null;
        }

        s = s.ToLowerInvariant();
        return s.EndsWith(".exe", StringComparison.Ordinal) ? s : s + ".exe";
    }
}

/// <summary>Accent color encodings (docs/integrations/windows-sources.md §8.4).</summary>
public static class AccentColors
{
    /// <summary><c>DwmGetColorizationColor</c>: <c>0xAARRGGBB</c>.</summary>
    public static string FromArgb(uint argb) => $"#{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}";

    /// <summary>Registry <c>Explorer\Accent\AccentColorMenu</c>: <c>0xAABBGGRR</c>.</summary>
    public static string FromAbgr(uint abgr) => $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";

    public static string FromRgb(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}

/// <summary>A screen rectangle in physical pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public bool Covers(ScreenRect other) => Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
}

/// <summary>Full-screen detection (docs/integrations/windows-sources.md §8.5).</summary>
public static class FullscreenDetector
{
    // QUERY_USER_NOTIFICATION_STATE
    public const int QunsBusy = 2;
    public const int QunsRunningD3DFullScreen = 3;
    public const int QunsApp = 7;

    /// <summary>
    /// True when the shell reports a full-screen app (<c>QUNS_BUSY</c>, exclusive D3D, full-screen Store app) or the
    /// foreground window — not the desktop or the shell — covers its whole monitor (borderless full screen).
    /// </summary>
    public static bool IsFullscreen(int? notificationState, ScreenRect? window, ScreenRect? monitor, bool isShellWindow)
    {
        if (notificationState is QunsBusy or QunsRunningD3DFullScreen or QunsApp)
        {
            return true;
        }

        return !isShellWindow && window is { } w && monitor is { } m && w.Covers(m);
    }
}

/// <summary>
/// Max of the samples inside a sliding time window. The audio collector samples peak meters at ~30 Hz but
/// publishes at ~20 Hz and the session client keeps only the latest value per flush, so each published value
/// is the max of the last few samples: short transients survive the rate change.
/// </summary>
public sealed class PeakWindow
{
    private readonly TimeSpan _window;
    private readonly Queue<(TimeSpan At, double Value)> _samples = new();

    public PeakWindow(TimeSpan window) => _window = window;

    public void Add(TimeSpan now, double value)
    {
        _samples.Enqueue((now, value));
        Trim(now);
    }

    public double? Max(TimeSpan now)
    {
        Trim(now);
        return _samples.Count == 0 ? null : _samples.Max(s => s.Value);
    }

    public void Clear() => _samples.Clear();

    private void Trim(TimeSpan now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().At > _window)
        {
            _samples.Dequeue();
        }
    }
}

/// <summary>
/// Combines lock/unlock events with a periodic poll of the session state: events win immediately, and a poll
/// that disagrees shortly after an event is ignored (the polled state may lag the event).
/// </summary>
public sealed class LockStateArbiter
{
    private readonly TimeSpan _eventPriority;
    private TimeSpan _lastEvent = TimeSpan.MinValue;

    public LockStateArbiter(TimeSpan? eventPriority = null) => _eventPriority = eventPriority ?? TimeSpan.FromSeconds(3);

    public bool Locked { get; private set; }

    public void OnEvent(bool locked, TimeSpan now)
    {
        Locked = locked;
        _lastEvent = now;
    }

    /// <summary>Applies a polled state (null = unknown) and returns the resulting state.</summary>
    public bool OnPoll(bool? polled, TimeSpan now)
    {
        if (polled is { } p && (_lastEvent == TimeSpan.MinValue || now - _lastEvent >= _eventPriority))
        {
            Locked = p;
        }

        return Locked;
    }
}
