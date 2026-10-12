using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Push;

/// <summary>
/// Connects the <see cref="PushGateway"/> to the sensor hub for as long as it runs: pushes made by external
/// programs become sensors <c>push/{id}</c>.
/// </summary>
internal sealed class PushProvider : ISensorProvider
{
    private readonly PushGateway _gateway;
    private readonly double _staleAfterSeconds;
    private readonly int _maxSensors;

    public PushProvider(string id, PushGateway gateway, ProviderSettings settings)
    {
        Id = id;
        _gateway = gateway;
        _staleAfterSeconds = Math.Max(1, settings.GetDouble("staleAfter", 120));
        _maxSensors = Math.Max(1, settings.GetInt("maxSensors", 500));
    }

    public string Id { get; }

    /// <summary>The gateway this provider serves.</summary>
    internal PushGateway Gateway => _gateway;

    public async Task RunAsync(ISensorSink sink, IProviderStatus status, CancellationToken cancellationToken)
    {
        _gateway.Attach(sink, _staleAfterSeconds, _maxSensors);
        status.Report(ProviderHealth.Ok);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown: fall through to detach.
        }
        finally
        {
            _gateway.Detach(sink);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
