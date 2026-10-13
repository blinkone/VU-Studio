using System.Net;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VuStudio.Sources.Remote;

/// <summary>An agent found on the LAN. The token is never advertised: the user still has to enter it.</summary>
public sealed record DiscoveredAgent(
    string InstanceName,
    string Name,
    string Url,
    int Port,
    IReadOnlyList<string> Addresses,
    string? Version,
    string? Id,
    IReadOnlyDictionary<string, string> Txt);

/// <summary>Finds VU Studio agents. Implementations: mDNS (<see cref="MdnsAgentDiscovery"/>) or a fixed list.</summary>
public interface IAgentDiscovery
{
    /// <summary>Browses for agents for up to <paramref name="timeout"/> and returns what answered, sorted by name.</summary>
    Task<IReadOnlyList<DiscoveredAgent>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Collects mDNS records (PTR, SRV, TXT, A/AAAA) and joins them into <see cref="DiscoveredAgent"/>s.</summary>
public sealed class AgentRecordCollector
{
    public const string ServiceType = "_vustudio._tcp";

    private readonly Dictionary<string, string> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Target, int Port)> _srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<IPAddress>> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Feeds the answer and additional records of one mDNS message.</summary>
    public void Add(IEnumerable<ResourceRecord> records)
    {
        lock (_gate)
        {
            var list = records.ToList();
            // Addresses in the same packet as an SRV record belong to its instance even when the target name does not resolve.
            var packetAddresses = list.OfType<AddressRecord>().Select(a => a.Address).ToList();
            foreach (var record in list)
            {
                switch (record)
                {
                    case PTRRecord ptr when ptr.Name.ToString().TrimEnd('.').EndsWith(ServiceType + ".local", StringComparison.OrdinalIgnoreCase):
                        _instances[ptr.DomainName.ToString().TrimEnd('.')] = ptr.DomainName.ToString().TrimEnd('.');
                        break;
                    case SRVRecord srv:
                        var key = srv.Name.ToString().TrimEnd('.');
                        _srv[key] = (srv.Target.ToString().TrimEnd('.'), srv.Port);
                        foreach (var address in packetAddresses)
                        {
                            Address(key, address);
                        }

                        break;
                    case TXTRecord txt:
                        _txt[txt.Name.ToString().TrimEnd('.')] = txt.Strings.ToList();
                        break;
                    case AddressRecord a:
                        Address(a.Name.ToString().TrimEnd('.'), a.Address);
                        break;
                }
            }
        }
    }

    private void Address(string name, IPAddress address)
    {
        if (!_addresses.TryGetValue(name, out var set))
        {
            _addresses[name] = set = [];
        }

        set.Add(address);
    }

    /// <summary>Instances seen but still lacking an SRV record or an address (worth a follow-up query).</summary>
    public IReadOnlyList<string> Unresolved()
    {
        lock (_gate)
        {
            return _instances.Keys.Where(i => !_srv.ContainsKey(i) || Addresses(i).Count == 0).ToList();
        }
    }

    public IReadOnlyList<DiscoveredAgent> Results()
    {
        lock (_gate)
        {
            var agents = new List<DiscoveredAgent>();
            foreach (var instance in _instances.Keys)
            {
                if (!_srv.TryGetValue(instance, out var srv))
                {
                    continue;
                }

                var addresses = Addresses(instance);
                if (addresses.Count == 0)
                {
                    continue;
                }

                var txt = ParseTxt(_txt.GetValueOrDefault(instance) ?? []);
                if (txt.TryGetValue("role", out var role) && !role.Equals("agent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var label = instance.Split('.')[0];
                var host = addresses[0].Contains(':') ? $"[{addresses[0]}]" : addresses[0];
                agents.Add(new DiscoveredAgent(
                    instance,
                    txt.GetValueOrDefault("name") ?? label,
                    $"http://{host}:{srv.Port}",
                    srv.Port,
                    addresses,
                    txt.GetValueOrDefault("v") ?? txt.GetValueOrDefault("ver"),
                    txt.GetValueOrDefault("id"),
                    txt));
            }

            return agents.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>IPv4 first (agents listen on IPv4), link-local IPv6 last and without a scope id.</summary>
    private List<string> Addresses(string instance)
    {
        var all = new HashSet<IPAddress>();
        if (_addresses.TryGetValue(instance, out var direct))
        {
            all.UnionWith(direct);
        }

        if (_srv.TryGetValue(instance, out var srv) && _addresses.TryGetValue(srv.Target, out var viaTarget))
        {
            all.UnionWith(viaTarget);
        }

        return all
            .Where(a => !IPAddress.IsLoopback(a) && !(a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal))
            .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(a => a.ToString(), StringComparer.Ordinal)
            .Select(a => a.ToString())
            .ToList();
    }

    public static Dictionary<string, string> ParseTxt(IEnumerable<string> strings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in strings)
        {
            var eq = s.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                result[s[..eq].Trim()] = s[(eq + 1)..];
            }
        }

        return result;
    }
}

/// <summary>Browses mDNS for <c>_vustudio._tcp</c>. Safe to call on machines without multicast: it simply finds nothing.</summary>
public sealed class MdnsAgentDiscovery : IAgentDiscovery
{
    private readonly ILogger _logger;

    public MdnsAgentDiscovery(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<IReadOnlyList<DiscoveredAgent>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var collector = new AgentRecordCollector();
        try
        {
            using var mdns = new MulticastService();
            var serviceName = new DomainName(AgentRecordCollector.ServiceType + ".local");
            mdns.AnswerReceived += (_, e) => collector.Add(e.Message.Answers.Concat(e.Message.AdditionalRecords));
            mdns.NetworkInterfaceDiscovered += (_, _) => mdns.SendQuery(serviceName, DnsClass.IN, DnsType.PTR);
            mdns.Start();
            try
            {
                mdns.SendQuery(serviceName, DnsClass.IN, DnsType.PTR);
                var half = TimeSpan.FromTicks(timeout.Ticks / 2);
                await Task.Delay(half, cancellationToken).ConfigureAwait(false);
                foreach (var instance in collector.Unresolved())
                {
                    var name = new DomainName(instance);
                    mdns.SendQuery(name, DnsClass.IN, DnsType.SRV);
                    mdns.SendQuery(name, DnsClass.IN, DnsType.TXT);
                    mdns.SendQuery(name, DnsClass.IN, DnsType.A);
                }

                await Task.Delay(timeout - half, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                mdns.Stop();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "mDNS browsing failed; use the manual address instead");
        }

        return collector.Results();
    }
}

/// <summary>"Discovery" of a user-entered list of addresses, so the UI can treat manual entries like discovered ones.</summary>
public sealed class StaticAgentDiscovery(IEnumerable<string> urls) : IAgentDiscovery
{
    public Task<IReadOnlyList<DiscoveredAgent>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var agents = new List<DiscoveredAgent>();
        foreach (var text in urls)
        {
            if (NetHttp.NormalizeBase(text) is { } uri)
            {
                agents.Add(new DiscoveredAgent(uri.Authority, uri.Host, uri.GetLeftPart(UriPartial.Authority), uri.Port, [uri.Host], null, null, new Dictionary<string, string>()));
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredAgent>>(agents);
    }
}
