using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;
using VuStudio.Session;
using VuStudio.Session.Media;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace VuStudio.Tray.Collectors;

/// <summary>
/// The system media session (GSMTC, docs/integrations/windows-sources.md §7): title, artist, album, player,
/// playing state, position/duration/progress (extrapolated while playing, §7.3) and the dominant color of the
/// album art. WinRT events only flag a refresh; one async loop does every read (debounced 200 ms, plus a 5 s
/// safety refresh) and publishes the position at 4 Hz from cached values.
/// </summary>
internal sealed class MediaCollector(ISessionPublisher publisher, ILogger logger) : Collector(publisher, logger)
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SafetyRefresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TextKeepAlive = TimeSpan.FromSeconds(5);
    private const int ThumbnailSize = 32;
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _refreshRequestedTicks = 0; // 0 = refresh now
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private State? _state;
    private State? _published;
    private TimeSpan _lastRefresh;
    private TimeSpan _lastText;
    private (string?, string?, string?) _colorKey;
    private string? _color;
    private string? _publishedColor;
    private bool _colorPending;

    public override string Name => "Media";

    protected override IReadOnlyList<SensorInfo> Sensors => SessionSensors.Media;

    protected override async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(Tick);
            do
            {
                try
                {
                    if (_manager is null)
                    {
                        await ConnectAsync().ConfigureAwait(false);
                    }

                    var now = _clock.Elapsed;
                    var requested = Interlocked.Read(ref _refreshRequestedTicks);
                    if (_manager is not null && ((requested >= 0 && now - TimeSpan.FromTicks(requested) >= Debounce) || now - _lastRefresh >= SafetyRefresh))
                    {
                        Interlocked.CompareExchange(ref _refreshRequestedTicks, -1, requested);
                        _lastRefresh = now;
                        await RefreshAsync(ct).ConfigureAwait(false);
                    }

                    PublishState(now);
                    ReportSuccess();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ReportFailure(ex, "reading the media session");
                    _lastRefresh = _clock.Elapsed;
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            Bind(null);
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= OnManagerChanged;
                _manager.SessionsChanged -= OnManagerChanged;
                _manager = null;
            }

            InvalidateAll();
        }
    }

    private async Task ConnectAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        manager.CurrentSessionChanged += OnManagerChanged;
        manager.SessionsChanged += OnManagerChanged;
        _manager = manager;
        Logger.LogInformation("Media session manager ready");
        RequestRefresh();
    }

    private void OnManagerChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) => RequestRefresh();

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => RequestRefresh();

    /// <summary>Any thread: only records when a refresh was asked for.</summary>
    private void RequestRefresh() => Interlocked.Exchange(ref _refreshRequestedTicks, _clock.Elapsed.Ticks);

    private async Task RefreshAsync(CancellationToken ct)
    {
        var session = PickSession(_manager!);
        Bind(session);
        if (session is null)
        {
            _state = null;
            return;
        }

        var properties = await session.TryGetMediaPropertiesAsync().AsTask(ct).ConfigureAwait(false);
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var app = MediaAppName.Friendly(session.SourceAppUserModelId);

        _state = new State(
            Clean(properties?.Title),
            Clean(properties?.Artist) ?? Clean(properties?.AlbumArtist),
            Clean(properties?.AlbumTitle),
            app,
            playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            playback?.PlaybackRate,
            timeline?.StartTime ?? TimeSpan.Zero,
            timeline?.EndTime ?? TimeSpan.Zero,
            timeline?.Position ?? TimeSpan.Zero,
            timeline?.LastUpdatedTime ?? default);

        // Album art: decode only when the track changes, or when the thumbnail arrives late.
        var key = (_state.Title, _state.Artist, session.SourceAppUserModelId);
        if (key != _colorKey || _colorPending)
        {
            if (key != _colorKey)
            {
                _color = null;
            }

            _colorKey = key;
            var thumbnail = properties?.Thumbnail;
            _colorPending = thumbnail is null;
            if (thumbnail is not null)
            {
                _color = await ReadColorAsync(thumbnail, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>With several players, prefer one that is playing, then the system's current session (§7.2).</summary>
    private static GlobalSystemMediaTransportControlsSession? PickSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        foreach (var s in sessions)
        {
            if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return s;
            }
        }

        return manager.GetCurrentSession() ?? sessions.FirstOrDefault();
    }

    private void Bind(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }

        _session = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnSessionChanged;
            session.PlaybackInfoChanged += OnSessionChanged;
            session.TimelinePropertiesChanged += OnSessionChanged;
            Logger.LogInformation("Media session: {App}", session.SourceAppUserModelId);
        }
    }

    private void PublishState(TimeSpan now)
    {
        var state = _state;
        if (state is null)
        {
            if (_published is not null || now - _lastText >= TextKeepAlive)
            {
                _published = null;
                _lastText = now;
                foreach (var id in new[] { SessionSensors.MediaTitle, SessionSensors.MediaArtist, SessionSensors.MediaAlbum, SessionSensors.MediaApp, SessionSensors.MediaPosition, SessionSensors.MediaDuration, SessionSensors.MediaProgress, SessionSensors.MediaColor })
                {
                    Invalidate(id);
                }
            }

            Publish(SessionSensors.MediaPlaying, false);
            return;
        }

        if (!Equals(state, _published) || _color != _publishedColor || now - _lastText >= TextKeepAlive)
        {
            _published = state;
            _publishedColor = _color;
            _lastText = now;
            PublishText(SessionSensors.MediaTitle, state.Title);
            PublishText(SessionSensors.MediaArtist, state.Artist);
            PublishText(SessionSensors.MediaAlbum, state.Album);
            PublishText(SessionSensors.MediaApp, state.App);
            PublishText(SessionSensors.MediaColor, _color);
        }

        Publish(SessionSensors.MediaPlaying, state.Playing);
        var position = MediaTimeline.Compute(state.Start, state.End, state.Position, state.LastUpdated, state.Playing, state.Rate, DateTimeOffset.UtcNow);
        PublishOrInvalidate(SessionSensors.MediaPosition, position.PositionSeconds);
        PublishOrInvalidate(SessionSensors.MediaDuration, position.DurationSeconds);
        PublishOrInvalidate(SessionSensors.MediaProgress, position.Progress);
    }

    private void PublishOrInvalidate(string id, double? value)
    {
        if (value is { } v)
        {
            Publish(id, v);
        }
        else
        {
            Invalidate(id);
        }
    }

    /// <summary>Decodes the thumbnail with System.Drawing, downscaled to 32×32 first, then <see cref="DominantColor"/>.</summary>
    private async Task<string?> ReadColorAsync(IRandomAccessStreamReference thumbnail, CancellationToken ct)
    {
        try
        {
            using var source = await thumbnail.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
            await using var stream = source.AsStreamForRead();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            if (buffer.Length is 0 or > MaxThumbnailBytes)
            {
                return null;
            }

            buffer.Position = 0;
            using var image = Image.FromStream(buffer, useEmbeddedColorManagement: false, validateImageData: true);
            using var small = new Bitmap(ThumbnailSize, ThumbnailSize, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, new Rectangle(0, 0, ThumbnailSize, ThumbnailSize));
            }

            var rect = new Rectangle(0, 0, ThumbnailSize, ThumbnailSize);
            var data = small.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[ThumbnailSize * ThumbnailSize * 4];
                for (var y = 0; y < ThumbnailSize; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (y * data.Stride), bytes, y * ThumbnailSize * 4, ThumbnailSize * 4);
                }

                // Format32bppArgb is B, G, R, A in memory.
                return DominantColor.FromBgra(bytes);
            }
            finally
            {
                small.UnlockBits(data);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not read the album art");
            return null;
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed record State(
        string? Title,
        string? Artist,
        string? Album,
        string? App,
        bool Playing,
        double? Rate,
        TimeSpan Start,
        TimeSpan End,
        TimeSpan Position,
        DateTimeOffset LastUpdated);
}
