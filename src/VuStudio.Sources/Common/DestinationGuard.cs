using System.Net;
using System.Net.Sockets;
using VuStudio.Core.Providers;

namespace VuStudio.Sources.Common;

/// <summary>
/// Outgoing HTTP for sources whose settings were last changed from the LAN (<see cref="ProviderInstanceConfig.ChangedRemotely"/>):
/// loopback, link-local (including cloud metadata at 169.254.169.254) and unspecified addresses are refused, so a
/// LAN user cannot turn the source into a proxy towards services that only listen on this machine. The check runs
/// on the addresses actually connected to (after DNS, for every redirect), so DNS tricks do not get around it.
/// </summary>
internal static class DestinationGuard
{
    /// <summary>True for addresses a LAN-configured source must not connect to.</summary>
    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254);
        }

        return false;
    }

    /// <summary>A private client that refuses forbidden destinations. Dispose it with the provider.</summary>
    public static HttpClient CreateRestrictedClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),

            // A proxy would make the connection on our behalf, out of reach of the check below.
            UseProxy = false,
            ConnectCallback = ConnectAsync,
        };
        return new HttpClient(OutboundMarker.Wrap(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0 || addresses.Any(IsForbidden))
        {
            throw new HttpRequestException(
                $"'{host}' points to this computer or to a link-local address. Sources set up from another device cannot call it; change the source on this PC.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
