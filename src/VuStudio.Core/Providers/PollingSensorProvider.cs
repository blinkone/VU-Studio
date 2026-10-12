using Microsoft.Extensions.Logging;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.Providers;

/// <summary>
/// Base class for providers that sample their source on a fixed interval.
/// Override <see cref="InitializeAsync"/> to define sensors once and <see cref="PollAsync"/> to publish values.
/// A failing poll marks the provider <see cref="ProviderHealth.Degraded"/> and keeps going;
/// exceptions thrown by <see cref="InitializeAsync"/> propagate (the manager restarts with backoff).
/// </summary>
public abstract class PollingSensorProvider : ISensorProvider
{
    private int _consecutiveFailures;

    protected PollingSensorProvider(string id, TimeSpan interval, ILogger logger)
    {
        Id = id;
        Interval = interval < TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : interval;
        Logger = logger;
    }

    public string Id { get; }

    protected TimeSpan Interval { get; }

    protected ILogger Logger { get; }

    public async Task RunAsync(ISensorSink sink, IProviderStatus status, CancellationToken cancellationToken)
    {
        status.Report(ProviderHealth.Starting);
        await InitializeAsync(sink, cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await PollAsync(sink, cancellationToken).ConfigureAwait(false);
                if (_consecutiveFailures > 0 || !_reportedOk)
                {
                    _consecutiveFailures = 0;
                    _reportedOk = true;
                    status.Report(ProviderHealth.Ok);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _reportedOk = false;
                status.Report(ProviderHealth.Degraded, ex.Message);
                if (_consecutiveFailures is 1 or 10 or 100 || _consecutiveFailures % 1000 == 0)
                {
                    Logger.LogWarning(ex, "Provider {ProviderId} poll failed ({Count} in a row)", Id, _consecutiveFailures);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private bool _reportedOk;

    /// <summary>Called once before the first poll; typically defines the sensors.</summary>
    protected virtual Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task PollAsync(ISensorSink sink, CancellationToken cancellationToken);

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
