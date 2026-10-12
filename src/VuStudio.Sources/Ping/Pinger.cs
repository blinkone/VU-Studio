using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VuStudio.Sources.Pings;

/// <summary>Outcome of one echo request.</summary>
/// <param name="Success">An echo reply came back.</param>
/// <param name="RoundTripMs">Round-trip time in milliseconds; only meaningful when <paramref name="Success"/> is true.</param>
/// <param name="Error">Why the request could not be made at all (unknown host, no permission…); <c>null</c> for a plain "no reply".</param>
internal readonly record struct PingOutcome(bool Success, double RoundTripMs, string? Error = null)
{
    public static PingOutcome Reply(double roundTripMs) => new(true, roundTripMs);

    /// <summary>Timed out or the network said unreachable: an expected outcome, not a fault of the provider.</summary>
    public static PingOutcome NoReply() => new(false, 0);

    /// <summary>The request could not be sent.</summary>
    public static PingOutcome Failure(string error) => new(false, 0, error);
}

/// <summary>Sends ICMP echo requests. Abstracted so that the provider can be tested without a network.</summary>
internal interface IPinger
{
    Task<PingOutcome> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Finds the address of the default gateway, if any.</summary>
internal interface IGatewayLocator
{
    IPAddress? FindDefaultGateway();
}

/// <summary>Pings through <see cref="Ping"/>, which uses ICMP sockets (or the system's ping program on Linux without privileges).</summary>
internal sealed class SystemPinger : IPinger
{
    public async Task<PingOutcome> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout, buffer: null, options: null, cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? PingOutcome.Reply(reply.RoundtripTime) : PingOutcome.NoReply();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PingException ex)
        {
            // Wraps the real cause: an unknown host name, or "operation not permitted" without ICMP rights.
            return PingOutcome.Failure(ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            return PingOutcome.Failure(ex.Message);
        }
    }
}

/// <summary>
/// Reads the default gateway from the network interfaces that are up. The operating system does not say
/// which route wins when there are several, so the first IPv4 gateway of a real interface is used.
/// </summary>
internal sealed class NetworkInterfaceGatewayLocator : IGatewayLocator
{
    public IPAddress? FindDefaultGateway()
    {
        try
        {
            var gateways = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
                .ToList();

            return gateways.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? gateways.FirstOrDefault();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
