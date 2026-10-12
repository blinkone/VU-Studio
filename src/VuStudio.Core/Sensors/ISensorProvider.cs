namespace VuStudio.Core.Sensors;

/// <summary>
/// Write side of the sensor hub, scoped to one provider instance. Ids passed here are <b>local</b>
/// paths (e.g. <c>cpu/0/load</c>); the hub turns them into <c>{providerId}/{localPath}</c>.
/// Implementations are thread-safe.
/// </summary>
public interface ISensorSink
{
    string ProviderId { get; }

    /// <summary>Adds or replaces the metadata of a sensor. <see cref="SensorInfo.Id"/> is the local path.</summary>
    void Define(SensorInfo info);

    void Remove(string localId);

    /// <summary>Removes every sensor of this provider.</summary>
    void Clear();

    void Publish(string localId, double value);

    void PublishText(string localId, string? text);

    /// <summary>Marks a sensor as having no current value (source offline).</summary>
    void Invalidate(string localId);
}

/// <summary>Health of a provider instance, shown in the UI.</summary>
public enum ProviderHealth
{
    Starting,
    Ok,

    /// <summary>Running, but some data is missing (e.g. one ping target unreachable).</summary>
    Degraded,

    /// <summary>Cannot run on this machine or a dependency is missing (e.g. HWiNFO not running).</summary>
    Unavailable,

    /// <summary>Crashed; will be restarted with backoff.</summary>
    Error,

    Stopped,
}

public interface IProviderStatus
{
    void Report(ProviderHealth health, string? message = null);
}

/// <summary>
/// A running data source. <see cref="RunAsync"/> is long-running: it publishes through the sink
/// until <paramref name="cancellationToken"/> is cancelled. If it throws, the provider manager logs the
/// error and restarts the instance with exponential backoff.
/// </summary>
public interface ISensorProvider : IAsyncDisposable
{
    /// <summary>Instance id (matches <c>ProviderInstanceConfig.Id</c>).</summary>
    string Id { get; }

    Task RunAsync(ISensorSink sink, IProviderStatus status, CancellationToken cancellationToken);
}
