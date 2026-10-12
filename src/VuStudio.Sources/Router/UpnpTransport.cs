using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace VuStudio.Sources.Router;

/// <summary>The network operations of router discovery, behind an interface so that tests can fake them.</summary>
internal interface IUpnpTransport
{
    /// <summary>SSDP M-SEARCH on every IPv4 interface.</summary>
    Task<IReadOnlyList<SsdpResponse>> MulticastSearchAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>The same M-SEARCH sent directly to one address (UDP 1900): reaches routers behind another router.</summary>
    Task<IReadOnlyList<SsdpResponse>> UnicastSearchAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>The default IPv4 gateway of this PC, if any.</summary>
    IPAddress? DefaultGateway();

    /// <summary>The second hop towards the internet (ICMP TTL 2): the ISP router behind a Nest Wifi.</summary>
    Task<IPAddress?> SecondHopAsync(CancellationToken cancellationToken);
}

internal sealed class SystemUpnpTransport : IUpnpTransport
{
    private static readonly IPEndPoint SsdpMulticast = new(IPAddress.Parse("239.255.255.250"), 1900);

    public async Task<IReadOnlyList<SsdpResponse>> MulticastSearchAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();

        var tasks = addresses.Select(a => SearchAsync(a, SsdpMulticast, timeout, cancellationToken)).ToList();
        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Distinct(all.SelectMany(r => r));
    }

    public Task<IReadOnlyList<SsdpResponse>> UnicastSearchAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken) =>
        SearchAsync(IPAddress.Any, new IPEndPoint(address, 1900), timeout, cancellationToken);

    private static async Task<IReadOnlyList<SsdpResponse>> SearchAsync(IPAddress local, IPEndPoint target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var found = new List<SsdpResponse>();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(local, 0));
            if (target.Address.Equals(SsdpMulticast.Address))
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            }

            foreach (var st in SsdpResponse.SearchTargets)
            {
                var payload = Encoding.ASCII.GetBytes(SsdpResponse.BuildSearch(st));
                await socket.SendToAsync(payload, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var buffer = new byte[4096];
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, deadline.Token).ConfigureAwait(false);
                if (SsdpResponse.Parse(Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes)) is { } response)
                {
                    found.Add(response);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // the search window ended
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException or PlatformNotSupportedException)
        {
            // an adapter that cannot do multicast (VPN, Hyper-V…): skip it
        }

        return found;
    }

    private static List<SsdpResponse> Distinct(IEnumerable<SsdpResponse> responses) =>
        responses.GroupBy(r => r.Location.AbsoluteUri, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    public IPAddress? DefaultGateway()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse("1.1.1.1"), 53); // sends nothing; only selects the outgoing interface
            var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.UnicastAddresses.Any(u => u.Address.Equals(local)))
                .SelectMany(p => p.GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            return null;
        }
    }

    public async Task<IPAddress?> SecondHopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(IPAddress.Parse("1.1.1.1"), TimeSpan.FromSeconds(2), new byte[32], new PingOptions(2, true), cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.TtlExpired && reply.Address is { } a && IsPrivate(a) ? a : null;
        }
        catch (Exception ex) when (ex is PingException or SocketException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork &&
               (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }
}
