using System.Security.Cryptography;

namespace VuStudio.Core.Providers;

/// <summary>
/// Marks every HTTP request the process's own sources send, so the service can recognise and refuse a source that
/// was pointed at the service's own API (SSRF to itself: a LAN user could otherwise use, say, an HTTP JSON source to
/// reach endpoints that trust loopback callers). The value is random per process; the service refuses any request
/// that carries the header, whatever its value.
/// </summary>
public static class OutboundMarker
{
    public const string HeaderName = "X-VuStudio-Outbound";

    /// <summary>Random per process; not a secret, it only makes the header easy to spot in logs.</summary>
    public static string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Sets the marker on <paramref name="request"/>, replacing any value a user-configured header gave it.</summary>
    public static void Apply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, Value);
    }

    /// <summary>Wraps <paramref name="inner"/> in a handler that marks every request.</summary>
    public static HttpMessageHandler Wrap(HttpMessageHandler inner) => new OutboundMarkerHandler { InnerHandler = inner };
}

/// <summary>Adds <see cref="OutboundMarker.HeaderName"/> to every request (see <see cref="OutboundMarker"/>).</summary>
public sealed class OutboundMarkerHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        OutboundMarker.Apply(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        OutboundMarker.Apply(request);
        return base.Send(request, cancellationToken);
    }
}
