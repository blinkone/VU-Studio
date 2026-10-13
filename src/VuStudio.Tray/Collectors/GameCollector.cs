using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;
using VuStudio.Session;
using VuStudio.Session.Rtss;

namespace VuStudio.Tray.Collectors;

/// <summary>
/// <c>session/game/fps</c>, <c>frametime</c> (ms) and <c>app</c> from RivaTuner Statistics Server's shared memory
/// (docs/integrations/windows-sources.md §2), read here because the mapping lives in the interactive session's
/// namespace. 4 Hz. Nothing is reported while RTSS is not running or no 3D application is live.
/// </summary>
internal sealed class GameCollector(ISessionPublisher publisher, ILogger logger, Func<uint> foregroundPid) : PollingCollector(publisher, logger)
{
    private static readonly string[] MappingNames = ["RTSSSharedMemoryV2", @"Global\RTSSSharedMemoryV2"];
    private static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(5);

    private readonly RtssTracker _tracker = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private TimeSpan _nextOpen;
    private bool _reporting = true;
    private string? _lastApp;

    public override string Name => "Games (RTSS)";

    protected override IReadOnlyList<SensorInfo> Sensors => SessionSensors.Game;

    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(250);

    protected override ValueTask PollAsync(CancellationToken ct)
    {
        var now = _clock.Elapsed;
        if (_view is null && now >= _nextOpen)
        {
            _nextOpen = now + ReopenInterval;
            Open();
        }

        RtssSnapshot? snapshot = null;
        if (_view is not null)
        {
            snapshot = Read(_view);
            if (snapshot is null)
            {
                // Signature gone (0xDEAD while RTSS frees it, or RTSS exited): reopen later.
                Close();
            }
        }

        var stats = _tracker.Update(snapshot, now, foregroundPid());
        if (stats is null)
        {
            if (_reporting)
            {
                InvalidateAll();
                _reporting = false;
                _lastApp = null;
            }

            return ValueTask.CompletedTask;
        }

        _reporting = true;
        if (stats.App != _lastApp)
        {
            _lastApp = stats.App;
            Logger.LogInformation("RTSS: measuring {App}", stats.App);
        }

        Publish(SessionSensors.GameFps, stats.Fps);
        Publish(SessionSensors.GameFrameTime, stats.FrameTimeMs);
        PublishText(SessionSensors.GameApp, stats.App);
        return ValueTask.CompletedTask;
    }

    protected override void OnStopped() => Close();

    private void Open()
    {
        foreach (var name in MappingNames)
        {
            try
            {
                _file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                Logger.LogInformation("RTSS shared memory opened ({Name}, {Size} bytes)", name, _view.Capacity);
                return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
                Close();
            }
        }
    }

    private void Close()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
    }

    /// <summary>Parses straight from the mapped view (no copy of the multi-megabyte object).</summary>
    private static unsafe RtssSnapshot? Read(MemoryMappedViewAccessor view)
    {
        var handle = view.SafeMemoryMappedViewHandle;
        byte* pointer = null;
        handle.AcquirePointer(ref pointer);
        try
        {
            var length = (int)Math.Min(view.Capacity, int.MaxValue);
            return RtssReader.Parse(new ReadOnlySpan<byte>(pointer + view.PointerOffset, length));
        }
        finally
        {
            handle.ReleasePointer();
        }
    }
}
