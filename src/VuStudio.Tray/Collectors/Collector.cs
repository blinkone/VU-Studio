using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;
using VuStudio.Session;

namespace VuStudio.Tray.Collectors;

/// <summary>
/// A source of <c>session/*</c> sensors. Each collector runs on its own task or thread, never on the UI
/// thread, catches and logs its own failures (rate-limited) and keeps retrying. Publishing only records
/// values in the <see cref="SessionClient"/>, which batches them to the service.
/// </summary>
internal abstract class Collector : IAsyncDisposable
{
    private readonly ISessionPublisher _publisher;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _failures;

    protected Collector(ISessionPublisher publisher, ILogger logger)
    {
        _publisher = publisher;
        Logger = logger;
    }

    public abstract string Name { get; }

    protected ILogger Logger { get; }

    /// <summary>Sensors defined when the collector starts.</summary>
    protected abstract IReadOnlyList<SensorInfo> Sensors { get; }

    public void Start()
    {
        if (_task is not null)
        {
            return;
        }

        if (Sensors.Count > 0)
        {
            _publisher.Define(SessionSensors.Provider, Sensors);
        }

        _cts = new CancellationTokenSource();
        OnStarting();
        _task = StartLoop(_cts.Token);
        Logger.LogInformation("{Collector} collector started", Name);
    }

    public async Task StopAsync()
    {
        if (_cts is null || _task is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("{Collector} collector did not stop within 5 s", Name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Collector} collector stopped with an error", Name);
        }
        finally
        {
            OnStopped();
            _cts.Dispose();
            _cts = null;
            _task = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Called on the starting thread (the UI thread) before the loop starts: subscribe to UI-affine events here.</summary>
    protected virtual void OnStarting()
    {
    }

    /// <summary>Called after the loop ended: unsubscribe events, release resources.</summary>
    protected virtual void OnStopped()
    {
    }

    /// <summary>Starts the background loop; the default runs <see cref="RunAsync"/> on the thread pool.</summary>
    protected virtual Task StartLoop(CancellationToken ct) => Task.Run(() => RunAsync(ct), CancellationToken.None);

    protected abstract Task RunAsync(CancellationToken ct);

    protected void Publish(string localId, double value) => _publisher.Publish(SessionSensors.Provider, localId, value);

    protected void Publish(string localId, bool value) => _publisher.Publish(SessionSensors.Provider, localId, value ? 1 : 0);

    protected void PublishText(string localId, string? text) => _publisher.Publish(SessionSensors.Provider, localId, text);

    protected void Invalidate(string localId) => _publisher.Invalidate(SessionSensors.Provider, localId);

    protected void InvalidateAll()
    {
        foreach (var sensor in Sensors)
        {
            Invalidate(sensor.Id);
        }
    }

    /// <summary>Logs the 1st, 10th, 100th and every 1000th consecutive failure.</summary>
    protected void ReportFailure(Exception ex, string what)
    {
        var n = Interlocked.Increment(ref _failures);
        if (n is 1 or 10 or 100 || n % 1000 == 0)
        {
            Logger.LogWarning(ex, "{Collector}: {What} failed ({Count} in a row)", Name, what, n);
        }
    }

    protected void ReportSuccess()
    {
        if (Interlocked.Exchange(ref _failures, 0) >= 10)
        {
            Logger.LogInformation("{Collector} recovered", Name);
        }
    }
}

/// <summary>A collector that samples on a fixed interval.</summary>
internal abstract class PollingCollector(ISessionPublisher publisher, ILogger logger) : Collector(publisher, logger)
{
    protected abstract TimeSpan Interval { get; }

    protected abstract ValueTask PollAsync(CancellationToken ct);

    protected override async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
                ReportSuccess();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ReportFailure(ex, "poll");
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }
}
