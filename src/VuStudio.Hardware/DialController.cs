using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Engine;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Hardware.Discovery;
using VuStudio.Hardware.Transport;

namespace VuStudio.Hardware;

/// <summary>
/// <see cref="IDialOutput"/> for the VU1 hub. It finds and opens the hub, enumerates the dials (keyed by UID,
/// because bus indices can change), reconnects with backoff, and sends desired-state differences from a single
/// worker loop, the only code that talks to the hub.
/// </summary>
/// <remarks>
/// <para>
/// Setters only record targets and wake the worker; they never block. Each worker cycle sends, in this order:
/// changed needles (at most one command per dial every <see cref="DialControllerOptions.NeedleInterval"/>), changed
/// backlights, queued operations (identify, raw moves, calibration, rescans), changed easing, and one step of a face
/// upload. After each image frame the upload pauses for <see cref="DialControllerOptions.InterChunkDelay"/> while
/// later cycles keep needles and backlights live. Only the latest value of anything is ever sent.
/// </para>
/// <para>
/// A dial whose command fails or times out is retried with exponential backoff; after five consecutive failures it
/// is reported offline and skipped until the next rescan. Losing the hub closes the connection and reconnects with
/// backoff; everything desired is sent again afterwards.
/// </para>
/// </remarks>
public sealed partial class DialController : IDialOutput, IAsyncDisposable
{
    private readonly ILogger<DialController> _logger;
    private readonly TimeProvider _time;
    private readonly Func<IVuTransport>? _transportFactory;
    private readonly IHubPortLocator _portLocator;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DialTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<WorkItem> _work = new();
    private readonly WorkSignal _signal;
    private DialControllerOptions _options;
    private HubStatus _status = HubStatus.Disconnected;
    private IReadOnlyList<DialInfo> _dials = [];
    private SimulatedHub? _simulator;
    private Connection? _connection;
    private Task? _runTask;
    private CancellationTokenSource? _runCts;
    private int _invalidation;
    private volatile bool _stopRequested;
    private volatile bool _reconnectRequested;
    private bool _disposed;

    /// <param name="options">Initial settings; see <see cref="ApplyOptions"/>.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Clock for all delays and timeouts; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="transportFactory">
    /// Creates the transport for every connection attempt, replacing discovery and the built-in simulator (tests).
    /// </param>
    /// <param name="portLocator">Replaces the portable <see cref="HubPortFinder"/>, e.g. with a SetupAPI locator on Windows.</param>
    public DialController(
        DialControllerOptions options,
        ILogger<DialController> logger,
        TimeProvider? time = null,
        Func<IVuTransport>? transportFactory = null,
        IHubPortLocator? portLocator = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _transportFactory = transportFactory;
        _portLocator = portLocator ?? new HubPortFinder(logger);
        _signal = new WorkSignal(_time);
        PortTransportFactory = port => new SerialVuTransport(port, _logger, _time);
    }

    /// <summary>Raised on the worker thread when <see cref="Status"/> or <see cref="Dials"/> change. Handlers must be quick.</summary>
    public event Action? Changed;

    public DialControllerOptions Options => Volatile.Read(ref _options);

    public HubStatus Status => Volatile.Read(ref _status);

    /// <summary>Every dial seen since start, ordered by bus index; dials that are gone or unresponsive are offline.</summary>
    public IReadOnlyList<DialInfo> Dials => Volatile.Read(ref _dials);

    /// <summary>The in-process hub while <see cref="DialControllerOptions.Simulate"/> is set; null otherwise.</summary>
    public SimulatedHub? Simulator => Volatile.Read(ref _simulator);

    /// <summary>Creates the transport for a discovered or configured port; tests replace the serial port.</summary>
    internal Func<string, IVuTransport> PortTransportFactory { get; init; }

    /// <summary>Starts the connection loop in the background; does not wait for the hub.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is not null)
            {
                return Task.CompletedTask;
            }

            _stopRequested = false;
            _reconnectRequested = false;
            var runCts = new CancellationTokenSource();
            _runCts = runCts;
            _runTask = Task.Run(() => RunAsync(runCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the worker after the current command: parks the needles and switches the lights off when
    /// <see cref="DialControllerOptions.ParkOnStop"/> is set, then closes the port. Cancelling
    /// <paramref name="cancellationToken"/> abandons the graceful part.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? run;
        CancellationTokenSource? runCts;
        lock (_lock)
        {
            run = _runTask;
            runCts = _runCts;
            if (run is null || runCts is null)
            {
                return;
            }

            _stopRequested = true;
        }

        _signal.Set();
        try
        {
            await run.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Stopping the dial controller gracefully was cancelled; stopping now");
            await runCts.CancelAsync().ConfigureAwait(false);
            await run.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (_runTask == run)
                {
                    _runTask = null;
                    _runCts = null;
                }
            }

            runCts.Dispose();
        }
    }

    /// <summary>
    /// Applies new settings. A different <see cref="DialControllerOptions.Port"/> or
    /// <see cref="DialControllerOptions.Simulate"/> reconnects; everything else applies from the next worker cycle.
    /// </summary>
    public void ApplyOptions(DialControllerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var previous = Interlocked.Exchange(ref _options, options);
        if (previous.RequiresReconnect(options))
        {
            _logger.LogInformation("Hub settings changed (port {Port}, simulate {Simulate}); reconnecting", options.Port, options.Simulate);
            _reconnectRequested = true;
        }

        _signal.Set();
    }

    public void SetNeedle(string uid, double percent)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        if (double.IsNaN(percent))
        {
            return;
        }

        var value = (int)Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero);
        UpdateTarget<int?>(uid, value, static t => t.Needle, static (t, v) => t.Needle = v);
    }

    /// <summary>Channels above 100 are clamped to 100.</summary>
    public void SetBacklight(string uid, LedLevels levels)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        var clamped = new LedLevels(Math.Min(levels.R, (byte)100), Math.Min(levels.G, (byte)100), Math.Min(levels.B, (byte)100), Math.Min(levels.W, (byte)100));
        UpdateTarget<LedLevels?>(uid, clamped, static t => t.Backlight, static (t, v) => t.Backlight = v);
    }

    public void SetBacklightTransition(string uid, int milliseconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        UpdateTarget<Easing?>(uid, Easing.ForTransition(Math.Max(0, milliseconds)), static t => t.BacklightEasing, static (t, v) => t.BacklightEasing = v);
    }

    /// <summary>The step is clamped to 1–100 % and the period to at least 1 ms.</summary>
    public void SetNeedleEasing(string uid, Easing easing)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentNullException.ThrowIfNull(easing);
        var normalized = new Easing(Math.Clamp(easing.Step, 1, 100), Math.Max(1, easing.PeriodMs));
        UpdateTarget<Easing?>(uid, normalized, static t => t.NeedleEasing, static (t, v) => t.NeedleEasing = v);
    }

    /// <summary>
    /// Records the face to show. The bitmap is encoded immediately, so the caller may reuse it. A bitmap whose
    /// <see cref="MonoBitmap.ComputeHash"/> equals the last uploaded one is not uploaded again.
    /// </summary>
    public void SetFace(string uid, MonoBitmap face)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentNullException.ThrowIfNull(face);
        var hash = face.ComputeHash();
        lock (_lock)
        {
            if (_targets.TryGetValue(uid, out var current) && current.Face?.Hash == hash)
            {
                return;
            }
        }

        UpdateTarget<FaceImage?>(uid, new FaceImage(EInkEncoder.Encode(face), hash), static t => t.Face, static (t, v) => t.Face = v);
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _invalidation);
        _signal.Set();
    }

    /// <summary>
    /// Wiggles the needle and flashes the light for <see cref="DialControllerOptions.IdentifyDuration"/>, then restores
    /// the desired state. Completes at once when the dial is not connected.
    /// </summary>
    public Task IdentifyAsync(string uid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        var item = new WorkItem((connection, self, _) => StartIdentify(connection, uid, self), cancellationToken);
        return Submit(item) ? item.Completion.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>
    /// Re-enumerates the bus (provisioning new dials first when <paramref name="provision"/> is set). While
    /// disconnected it triggers an immediate reconnect instead.
    /// </summary>
    /// <exception cref="VuHubException">The hub did not answer; the controller reconnects.</exception>
    public Task RescanAsync(bool provision, CancellationToken cancellationToken = default)
    {
        var item = new WorkItem((connection, self, ct) => RescanWorkAsync(connection, provision, self, ct), cancellationToken, throwOnFailure: true);
        if (Submit(item))
        {
            return item.Completion.WaitAsync(cancellationToken);
        }

        if (Status.State != HubConnectionState.Connecting)
        {
            _reconnectRequested = true;
            _signal.Set();
        }

        return Task.CompletedTask;
    }

    /// <summary>Moves the needle by raw DAC value (0–65535). False when the dial is not connected or the command failed.</summary>
    /// <remarks>The needle stays there until its desired percent changes or <see cref="Invalidate"/> is called.</remarks>
    public Task<bool> SetRawAsync(string uid, int raw, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentOutOfRangeException.ThrowIfNegative(raw);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(raw, VuHubClient.MaxRawValue);
        var item = new WorkItem((connection, self, ct) => RawWorkAsync(connection, uid, raw, self, ct), cancellationToken);
        return Submit(item) ? item.Completion.WaitAsync(cancellationToken) : Task.FromResult(false);
    }

    /// <summary>Stores the raw value of a calibration point (0–65535). False when the dial is not connected or the command failed.</summary>
    public Task<bool> CalibrateAsync(string uid, CalibrationPoint point, int raw, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentOutOfRangeException.ThrowIfNegative(raw);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(raw, VuHubClient.MaxRawValue);
        var item = new WorkItem((connection, self, ct) => CalibrateWorkAsync(connection, uid, point, raw, self, ct), cancellationToken);
        return Submit(item) ? item.Completion.WaitAsync(cancellationToken) : Task.FromResult(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private void UpdateTarget<T>(string uid, T value, Func<DialTarget, T> get, Action<DialTarget, T> set)
    {
        bool changed;
        lock (_lock)
        {
            if (!_targets.TryGetValue(uid, out var target))
            {
                target = new DialTarget();
                _targets[uid] = target;
            }

            changed = !EqualityComparer<T>.Default.Equals(get(target), value);
            if (changed)
            {
                set(target, value);
            }
        }

        if (changed)
        {
            _signal.Set();
        }
    }

    /// <summary>Queues work for the worker; false when there is no connection to run it on.</summary>
    private bool Submit(WorkItem item)
    {
        lock (_lock)
        {
            if (_connection is null)
            {
                return false;
            }

            _work.Enqueue(item);
        }

        _signal.Set();
        return true;
    }

    private void AbandonWork()
    {
        while (_work.TryDequeue(out var item))
        {
            item.Abandon(null);
        }
    }

    private void SetStatus(HubStatus status)
    {
        if (Equals(Volatile.Read(ref _status), status))
        {
            return;
        }

        Volatile.Write(ref _status, status);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A DialController.Changed handler failed");
            }
        }
    }

    /// <summary>What the engine wants for one dial; guarded by <see cref="_lock"/>.</summary>
    private sealed class DialTarget
    {
        public int? Needle { get; set; }

        public LedLevels? Backlight { get; set; }

        public Easing? NeedleEasing { get; set; }

        public Easing? BacklightEasing { get; set; }

        public FaceImage? Face { get; set; }
    }

    /// <summary>An encoded e-paper frame and the hash of the bitmap it came from.</summary>
    private sealed record FaceImage(byte[] Data, ulong Hash);
}
