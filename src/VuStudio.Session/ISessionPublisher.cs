using VuStudio.Core.Sensors;

namespace VuStudio.Session;

/// <summary>
/// Write side of the session channel (<c>/api/session</c>). Implemented by <see cref="SessionClient"/>;
/// <see cref="ForwardingSink"/> and <see cref="UserSessionProviderHost"/> depend only on this interface.
/// All members are thread-safe and never block on the network: they record the latest state, which a
/// background loop sends in coalesced batches.
/// </summary>
public interface ISessionPublisher
{
    /// <summary>Adds or replaces sensor definitions. <see cref="SensorInfo.Id"/> is the local id.</summary>
    void Define(string provider, IEnumerable<SensorInfo> sensors);

    /// <summary>Publishes a number. Non-finite values invalidate the sensor.</summary>
    void Publish(string provider, string localId, double value);

    /// <summary>Publishes a text or color value; <c>null</c> invalidates the sensor.</summary>
    void Publish(string provider, string localId, string? text);

    /// <summary>Marks a sensor as having no current value.</summary>
    void Invalidate(string provider, string localId);

    /// <summary>Forgets a sensor: it is invalidated on the service and no longer re-sent after a reconnect.</summary>
    void Remove(string provider, string localId);

    /// <summary>Forgets every sensor and the status of <paramref name="provider"/>.</summary>
    void ClearProvider(string provider);

    /// <summary>Reports the health of a provider (shown by the service UI).</summary>
    void ReportStatus(string provider, ProviderHealth health, string? message = null);
}

/// <summary>A notification sent by the service (alarms), to be shown as a desktop toast.</summary>
public sealed record SessionNotification(string Title, string Message, string? Icon);
