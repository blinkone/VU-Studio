using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>
/// Runs a set of <see cref="ISystemCollector"/>s on every tick. A collector that fails is logged (once, until
/// it works again) and skipped; the others carry on, because on the many shapes of Linux machines some
/// sources are always missing or unreadable and that is not a fault.
/// </summary>
internal sealed class SystemProvider : PollingSensorProvider
{
    private readonly IReadOnlyList<ISystemCollector> _collectors;
    private readonly TimeProvider _time;
    private readonly HashSet<ISystemCollector> _failing = [];
    private SensorRegistry? _sensors;
    private long _started;

    public SystemProvider(string id, IReadOnlyList<ISystemCollector> collectors, TimeSpan interval, TimeProvider time, ILogger logger)
        : base(id, interval, logger)
    {
        _collectors = collectors;
        _time = time;
    }

    protected override Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sensors = new SensorRegistry(sink, SensorHints.StaleAfterSeconds(Interval));
        _started = _time.GetTimestamp();
        return Task.CompletedTask;
    }

    protected override Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var sensors = _sensors ?? throw new InvalidOperationException("The provider was not initialised.");
        var context = new SampleContext(_time.GetElapsedTime(_started), _time.GetUtcNow());
        foreach (var collector in _collectors)
        {
            try
            {
                collector.Collect(sensors, context);
                _failing.Remove(collector);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_failing.Add(collector))
                {
                    Logger.LogWarning(ex, "System collector {Collector} failed; the others continue", collector.GetType().Name);
                }
            }
        }

        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        foreach (var collector in _collectors)
        {
            collector.Dispose();
        }

        return base.DisposeAsync();
    }
}
