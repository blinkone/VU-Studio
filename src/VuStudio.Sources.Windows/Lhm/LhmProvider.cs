using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Windows.Lhm;

/// <summary>
/// LibreHardwareMonitor provider. A dedicated <see cref="LhmWorker"/> thread does all LHM calls; this class is the
/// watchdog: it reports health, flags a slow update as degraded and throws when the update is stuck so that the
/// provider manager restarts the provider with a fresh <c>Computer</c>.
/// </summary>
internal sealed class LhmProvider : ISensorProvider
{
    private readonly Func<ILhmSession> _sessionFactory;
    private readonly LhmSettings _settings;
    private readonly LhmTimings _timings;
    private readonly LhmEnvironment _environment;
    private readonly ILogger _log;
    private LhmWorker? _worker;
    private int _disposed;

    public LhmProvider(string id, LhmSettings settings, LhmTimings timings, LhmEnvironment environment, Func<ILhmSession> sessionFactory, ILogger log)
    {
        Id = id;
        _settings = settings;
        _timings = timings;
        _environment = environment;
        _sessionFactory = sessionFactory;
        _log = log;
    }

    public string Id { get; }

    public async Task RunAsync(ISensorSink sink, IProviderStatus status, CancellationToken cancellationToken)
    {
        status.Report(ProviderHealth.Starting);
        var worker = new LhmWorker(_sessionFactory(), sink, _settings, _timings, _log);
        _worker = worker;
        var isElevated = SafeCall(_environment.IsElevated, true);
        worker.Start();

        using var timer = new PeriodicTimer(_timings.WatchTick);
        LhmStatus? last = null;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var progress = worker.GetProgress();
                LhmWorker.ThrowIfFaulted(progress);

                var stuckFor = progress.Phase is LhmPhase.Idle or LhmPhase.Stopped ? TimeSpan.Zero : worker.Elapsed(progress.PhaseStartedAt);
                var level = LhmHealth.Watchdog(progress.Phase, stuckFor, _timings);
                if (level == WatchdogLevel.Fail)
                {
                    worker.Abandon();
                    _log.LogError("LibreHardwareMonitor did not respond for {Seconds} s (phase {Phase}); restarting the provider",
                        (int)stuckFor.TotalSeconds, progress.Phase);
                    throw new TimeoutException($"LibreHardwareMonitor did not respond for {(int)stuckFor.TotalSeconds} s ({progress.Phase}).");
                }

                var pawnIoInstalled = progress.CpuTemperatureMisses >= _timings.NullCpuTemperaturePolls
                    && SafeCall(_environment.PawnIoInstalled, false);
                var next = LhmHealth.Evaluate(
                    level,
                    stuckFor,
                    progress.CompletedPasses > 0,
                    isElevated,
                    progress.CpuTemperatureMisses,
                    pawnIoInstalled,
                    progress.FailedHardware,
                    progress.LastError,
                    _timings);
                if (last != next)
                {
                    last = next;
                    status.Report(next.Health, next.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_worker is { } worker)
        {
            await worker.StopAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private static T SafeCall<T>(Func<T> func, T fallback)
    {
        try
        {
            return func();
        }
        catch
        {
            return fallback;
        }
    }
}
