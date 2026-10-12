using VuStudio.Core.Sensors;

namespace VuStudio.Session;

/// <summary>
/// An <see cref="ISensorSink"/> that forwards everything to the service over the session channel under
/// one provider id. It lets the tray run user-session providers (scripts) through the normal
/// <see cref="ISensorProvider"/> API: the service's hub adds the <c>{providerId}/</c> prefix as usual.
/// </summary>
public sealed class ForwardingSink : ISensorSink
{
    private readonly ISessionPublisher _publisher;

    public ForwardingSink(ISessionPublisher publisher, string providerId)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _publisher = publisher;
        ProviderId = providerId;
    }

    public string ProviderId { get; }

    public void Define(SensorInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        _publisher.Define(ProviderId, [info]);
    }

    public void Remove(string localId) => _publisher.Remove(ProviderId, localId);

    public void Clear() => _publisher.ClearProvider(ProviderId);

    public void Publish(string localId, double value) => _publisher.Publish(ProviderId, localId, value);

    public void PublishText(string localId, string? text) => _publisher.Publish(ProviderId, localId, text);

    public void Invalidate(string localId) => _publisher.Invalidate(ProviderId, localId);
}

/// <summary>Forwards a provider's health reports over the session channel and remembers the latest one.</summary>
public sealed class ForwardingStatus : IProviderStatus
{
    private readonly ISessionPublisher _publisher;
    private readonly string _providerId;
    private readonly Action<ProviderHealth, string?>? _observer;

    public ForwardingStatus(ISessionPublisher publisher, string providerId, Action<ProviderHealth, string?>? observer = null)
    {
        _publisher = publisher;
        _providerId = providerId;
        _observer = observer;
    }

    public void Report(ProviderHealth health, string? message = null)
    {
        _publisher.ReportStatus(_providerId, health, message);
        _observer?.Invoke(health, message);
    }
}
