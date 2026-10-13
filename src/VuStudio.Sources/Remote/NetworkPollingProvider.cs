using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Remote;

/// <summary>
/// Base class of the providers that read a device over the network. Compared with
/// <see cref="PollingSensorProvider"/> it adds: health reporting with <see cref="ProviderHealth.Unavailable"/>
/// for problems the user must fix, back-off and <c>Retry-After</c> handling, invalidation of every sensor
/// when the source stays unreachable, and a small API that defines sensors lazily, publishes values and
/// invalidates sensors that disappear.
/// </summary>
internal abstract class NetworkPollingProvider : ISensorProvider
{
    private const int FailuresBeforeInvalidate = 3;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _invalid = new(StringComparer.Ordinal);
    private readonly List<SweepScope> _scopes = [];
    private readonly IDisposable? _owner;
    private SensorRegistry? _registry;
    private IProviderStatus? _status;
    private int _failures;

    protected NetworkPollingProvider(string id, TimeSpan interval, TimeProvider time, ILogger logger, IDisposable? owner = null, double? staleAfterSeconds = null)
    {
        Id = id;
        Interval = interval < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : interval;
        Time = time;
        Logger = logger;
        _owner = owner;
        StaleAfterSeconds = staleAfterSeconds ?? SensorHints.StaleAfterSeconds(Interval);
    }

    public string Id { get; }

    protected TimeSpan Interval { get; }

    protected TimeProvider Time { get; }

    protected ILogger Logger { get; }

    protected double StaleAfterSeconds { get; }

    /// <summary>Monotonic clock for rate computations.</summary>
    protected TimeSpan Clock => Time.GetElapsedTime(0);

    /// <summary>Set during a poll to report <see cref="ProviderHealth.Degraded"/> although values were published (partial data).</summary>
    protected string? Warning { get; set; }

    protected int ConsecutiveFailures => _failures;

    protected SensorRegistry Registry => _registry ?? throw new InvalidOperationException("The provider is not running.");

    protected IProviderStatus Status => _status ?? throw new InvalidOperationException("The provider is not running.");

    public async Task RunAsync(ISensorSink sink, IProviderStatus status, CancellationToken cancellationToken)
    {
        Attach(sink, status);
        status.Report(ProviderHealth.Starting);
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Time.GetTimestamp();
            var delay = await StepAsync(cancellationToken).ConfigureAwait(false);
            var wait = delay - Time.GetElapsedTime(started);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, Time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Binds the provider to a sink; used by <see cref="RunAsync"/> and by tests that drive single steps.</summary>
    internal void Attach(ISensorSink sink, IProviderStatus status)
    {
        _registry = new SensorRegistry(sink, StaleAfterSeconds);
        _status = status;
    }

    /// <summary>Runs one poll with full error handling and returns the delay before the next one.</summary>
    internal async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        try
        {
            Warning = null;
            await PollAsync(cancellationToken).ConfigureAwait(false);
            _failures = 0;
            Status.Report(Warning is null ? ProviderHealth.Ok : ProviderHealth.Degraded, Warning);
            return Interval;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RateLimitedException ex)
        {
            Failed(ex, ProviderHealth.Degraded);
            return ex.RetryAfter > Interval ? ex.RetryAfter : Interval;
        }
        catch (SourceUnavailableException ex)
        {
            Failed(ex, ProviderHealth.Unavailable);
            return RetryCap;
        }
        catch (Exception ex)
        {
            Failed(ex, ProviderHealth.Degraded);
            var first = Interval < MaxBackoff ? Interval : MaxBackoff;
            var backoff = TimeSpan.FromTicks(first.Ticks * (1L << Math.Min(_failures, 6)));
            return backoff > RetryCap ? RetryCap : backoff;
        }
    }

    private void Failed(Exception ex, ProviderHealth health)
    {
        _failures++;
        Status.Report(health, ex.Message);
        if (_failures is 1 or 10 or 100)
        {
            Logger.LogWarning("Provider {ProviderId}: {Message} ({Count} in a row)", Id, ex.Message, _failures);
        }

        if (_failures >= FailuresBeforeInvalidate)
        {
            InvalidateAll();
        }
    }

    /// <summary>Longest wait between attempts while the source is failing: 30 s for fast sources, up to 5 min for slow ones.</summary>
    private TimeSpan RetryCap => TimeSpan.FromTicks(Math.Max(MaxBackoff.Ticks, Math.Min(Interval.Ticks, TimeSpan.FromMinutes(5).Ticks)));

    protected abstract Task PollAsync(CancellationToken cancellationToken);

    /// <summary>Called when the source has been unreachable for a while: forget state that cannot be trusted any more.</summary>
    protected virtual void OnSourceLost()
    {
    }

    protected void InvalidateAll()
    {
        if (_registry is null)
        {
            return;
        }

        foreach (var id in _registry.Ids.ToList())
        {
            SetNone(id);
        }

        OnSourceLost();
    }

    // ----- sensors -----

    protected void Num(SensorInfo info, double? value)
    {
        Registry.Define(info);
        Touch(info.Id);
        if (value is { } v && double.IsFinite(v))
        {
            Registry.Publish(info.Id, v);
            _invalid.Remove(info.Id);
        }
        else
        {
            SetNone(info.Id);
        }
    }

    protected void Txt(SensorInfo info, string? text)
    {
        Registry.Define(info);
        Touch(info.Id);
        if (text is null)
        {
            SetNone(info.Id);
        }
        else
        {
            Registry.PublishText(info.Id, text);
            _invalid.Remove(info.Id);
        }
    }

    /// <summary>Removes a sensor from the hub (it no longer exists at the source).</summary>
    protected void Forget(string localId)
    {
        Registry.Remove(localId);
        _invalid.Remove(localId);
    }

    /// <summary>Marks a sensor as having no value; the hub is only told once until it gets a value again.</summary>
    protected void SetNone(string localId)
    {
        if (_invalid.Add(localId))
        {
            Registry.Invalidate(localId);
        }
    }

    private void Touch(string id)
    {
        foreach (var scope in _scopes)
        {
            scope.Seen.Add(id);
        }
    }

    /// <summary>
    /// Opens a scope for a dynamic family of sensors (<c>fs/…</c>, <c>net/…</c>): when it is disposed, sensors of
    /// the family that were not published inside it are invalidated (a disk that was unplugged).
    /// </summary>
    protected IDisposable Sweep(string prefix) => Sweep(i => i.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Like <see cref="Sweep(string)"/> with an arbitrary id filter.</summary>
    protected IDisposable Sweep(Func<string, bool> belongs)
    {
        var scope = new SweepScope(this, belongs);
        _scopes.Add(scope);
        return scope;
    }

    private sealed class SweepScope(NetworkPollingProvider owner, Func<string, bool> belongs) : IDisposable
    {
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public void Dispose()
        {
            owner._scopes.Remove(this);
            foreach (var id in owner.Registry.Ids.Where(i => belongs(i) && !Seen.Contains(i)).ToList())
            {
                owner.SetNone(id);
            }
        }
    }

    /// <summary>Concise <see cref="SensorInfo"/> construction with the kind and icon derived from the unit and category.</summary>
    protected static SensorInfo Info(
        string id,
        string name,
        SensorCategory category,
        string unit = Units.None,
        string? device = null,
        string? host = null,
        string? kind = null,
        double? min = null,
        double? max = null,
        string? icon = null,
        bool text = false,
        double? staleAfterSeconds = null)
    {
        kind ??= text ? SensorKinds.Text : SensorHints.KindForUnit(unit);
        return new SensorInfo
        {
            Id = id,
            Name = name,
            Device = device,
            Host = host,
            Category = category,
            Kind = kind,
            Unit = text ? Units.None : unit,
            ValueKind = text ? SensorValueKind.Text : SensorValueKind.Number,
            Min = text ? null : min,
            Max = text ? null : max,
            Icon = icon ?? SensorHints.Icon(category, kind),
            StaleAfterSeconds = staleAfterSeconds,
        };
    }

    public virtual ValueTask DisposeAsync()
    {
        _owner?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
