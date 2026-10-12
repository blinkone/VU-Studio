namespace VuStudio.Session.Media;

/// <summary>Playback position derived from a media session timeline.</summary>
/// <param name="PositionSeconds">Elapsed time in the track, or <c>null</c>.</param>
/// <param name="DurationSeconds">Track length, or <c>null</c> when the app publishes none (live streams).</param>
/// <param name="Progress">0..100, or <c>null</c> without a duration.</param>
public readonly record struct MediaPosition(double? PositionSeconds, double? DurationSeconds, double? Progress);

/// <summary>
/// Timeline math for GSMTC (docs/integrations/windows-sources.md §7.3). <c>Position</c> is "current as of
/// <c>LastUpdatedTime</c>", and many players only update it on state changes, so while playing the position is
/// extrapolated from the last update at the playback rate.
/// </summary>
public static class MediaTimeline
{
    public static MediaPosition Compute(
        TimeSpan start,
        TimeSpan end,
        TimeSpan position,
        DateTimeOffset lastUpdated,
        bool playing,
        double? playbackRate,
        DateTimeOffset now)
    {
        var pos = position;
        if (playing && lastUpdated.Year > 1)
        {
            var elapsed = now - lastUpdated;
            if (elapsed > TimeSpan.Zero)
            {
                var rate = playbackRate is { } r && double.IsFinite(r) && r > 0 ? r : 1.0;
                pos += TimeSpan.FromTicks((long)(elapsed.Ticks * rate));
            }
        }

        var length = end - start;
        var elapsedInTrack = pos - start;
        if (length <= TimeSpan.Zero)
        {
            return new MediaPosition(elapsedInTrack >= TimeSpan.Zero ? Math.Round(elapsedInTrack.TotalSeconds, 1) : null, null, null);
        }

        if (elapsedInTrack < TimeSpan.Zero)
        {
            elapsedInTrack = TimeSpan.Zero;
        }
        else if (elapsedInTrack > length)
        {
            elapsedInTrack = length;
        }

        return new MediaPosition(
            Math.Round(elapsedInTrack.TotalSeconds, 1),
            Math.Round(length.TotalSeconds, 1),
            Math.Round(elapsedInTrack / length * 100, 2));
    }
}

/// <summary>Turns a media session's <c>SourceAppUserModelId</c> into a short app name.</summary>
public static class MediaAppName
{
    /// <summary>
    /// <c>Spotify.exe</c> → <c>Spotify</c>; <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c> → <c>Spotify</c>;
    /// <c>Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic</c> → <c>ZuneMusic</c>; <c>C:\x\vlc.exe</c> → <c>vlc</c>.
    /// </summary>
    public static string? Friendly(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        var s = appUserModelId.Trim();
        var bang = s.LastIndexOf('!');
        if (bang >= 0 && bang < s.Length - 1)
        {
            s = s[(bang + 1)..];
        }

        var slash = s.LastIndexOfAny(['\\', '/']);
        if (slash >= 0 && slash < s.Length - 1)
        {
            s = s[(slash + 1)..];
        }

        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }
        else if (bang >= 0)
        {
            var dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1)
            {
                s = s[(dot + 1)..];
            }
        }

        return s.Length == 0 ? appUserModelId.Trim() : s;
    }
}
