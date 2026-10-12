using System.Net.NetworkInformation;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>A network interface and its byte counters at one moment.</summary>
/// <param name="IsVirtual">Bridges, veth pairs, VPN and VM adapters: their traffic is also counted on a real interface.</param>
/// <param name="SpeedBitsPerSecond">Link speed when the system knows it.</param>
internal sealed record NetworkInterfaceSample(
    string Name,
    bool IsUp,
    bool IsLoopback,
    bool IsVirtual,
    long? SpeedBitsPerSecond,
    long BytesReceived,
    long BytesSent);

internal interface INetworkInterfaceReader
{
    IReadOnlyList<NetworkInterfaceSample> Read();
}

/// <summary>Reads interfaces through <see cref="NetworkInterface"/>.</summary>
internal sealed class SystemNetworkInterfaceReader : INetworkInterfaceReader
{
    private static readonly string[] VirtualDescriptions =
        ["Virtual", "Hyper-V", "VMware", "VirtualBox", "TAP-", "Pseudo-Interface", "Npcap", "WireGuard", "Tailscale", "ZeroTier"];

    public IReadOnlyList<NetworkInterfaceSample> Read()
    {
        var result = new List<NetworkInterfaceSample>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return result;
        }

        foreach (var nic in interfaces)
        {
            try
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var up = nic.OperationalStatus == OperationalStatus.Up;
                var loopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                if (!up || loopback)
                {
                    result.Add(new NetworkInterfaceSample(nic.Name, up, loopback, true, null, 0, 0));
                    continue;
                }

                var stats = nic.GetIPStatistics();
                result.Add(new NetworkInterfaceSample(
                    nic.Name,
                    up,
                    loopback,
                    IsVirtual(nic),
                    Speed(nic),
                    stats.BytesReceived,
                    stats.BytesSent));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                // An adapter that vanished while we looked at it.
            }
        }

        return result;
    }

    private static long? Speed(NetworkInterface nic)
    {
        try
        {
            return nic.Speed > 0 ? nic.Speed : null;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or IOException)
        {
            return null;
        }
    }

    private static bool IsVirtual(NetworkInterface nic)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var target = new DirectoryInfo(Path.Combine("/sys/class/net", nic.Name)).ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName.Contains("/devices/virtual/", StringComparison.Ordinal) == true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return VirtualDescriptions.Any(d => nic.Description.Contains(d, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Publishes the receive and transmit rate of every interface that is up (<c>net/{interface}/rx</c> and
/// <c>/tx</c>) and the totals over the real interfaces (<c>net/rx</c>, <c>net/tx</c>), in bytes per second.
/// </summary>
internal sealed class NetworkCollector(INetworkInterfaceReader reader) : ISystemCollector
{
    private readonly CounterRate _rates = new();
    private readonly Dictionary<string, string> _slugs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedSlugs = new(StringComparer.Ordinal) { "rx", "tx" };

    public void Collect(SensorRegistry sensors, SampleContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double realRx = 0, realTx = 0, allRx = 0, allTx = 0;
        var realCount = 0;
        var haveRates = false;

        foreach (var nic in reader.Read())
        {
            // veth* are the container ends of Docker's virtual cables: dozens of short-lived, useless sensors.
            if (!nic.IsUp || nic.IsLoopback || nic.Name.StartsWith("veth", StringComparison.Ordinal))
            {
                continue;
            }

            var slug = SlugFor(nic.Name);
            seen.Add(slug);
            var rx = _rates.Update($"net/{slug}/rx", nic.BytesReceived, context.Time);
            var tx = _rates.Update($"net/{slug}/tx", nic.BytesSent, context.Time);

            var max = nic.SpeedBitsPerSecond is { } bits ? bits / 8.0 : (double?)null;
            sensors.Define(Metrics.Number($"net/{slug}/rx", "Download", nic.Name, SensorCategory.Network, SensorKinds.Throughput, Units.BytesPerSecond, 0, max, "download"));
            sensors.Define(Metrics.Number($"net/{slug}/tx", "Upload", nic.Name, SensorCategory.Network, SensorKinds.Throughput, Units.BytesPerSecond, 0, max, "upload"));

            if (rx is { } r && tx is { } t)
            {
                sensors.Publish($"net/{slug}/rx", r);
                sensors.Publish($"net/{slug}/tx", t);
                haveRates = true;
                allRx += r;
                allTx += t;
                if (!nic.IsVirtual)
                {
                    realRx += r;
                    realTx += t;
                    realCount++;
                }
            }
        }

        foreach (var gone in _active.Except(seen).ToList())
        {
            sensors.RemovePrefix($"net/{gone}/");
            _rates.ForgetPrefix($"net/{gone}/");
        }

        _active.Clear();
        _active.UnionWith(seen);

        if (haveRates)
        {
            // Without any real interface (a container with only virtual ones) the totals fall back to everything.
            var (totalRx, totalTx) = realCount > 0 ? (realRx, realTx) : (allRx, allTx);
            Metrics.EnsureNumber(sensors, "net/rx", "Total download", "Network", SensorCategory.Network, SensorKinds.Throughput, Units.BytesPerSecond, 0, null, "download-network");
            Metrics.EnsureNumber(sensors, "net/tx", "Total upload", "Network", SensorCategory.Network, SensorKinds.Throughput, Units.BytesPerSecond, 0, null, "upload-network");
            sensors.Publish("net/rx", totalRx);
            sensors.Publish("net/tx", totalTx);
        }
    }

    public void Dispose()
    {
    }

    private string SlugFor(string name)
    {
        if (!_slugs.TryGetValue(name, out var slug))
        {
            slug = LocalIds.Unique(LocalIds.Slug(name, "iface"), _usedSlugs);
            _slugs[name] = slug;
        }

        return slug;
    }
}
