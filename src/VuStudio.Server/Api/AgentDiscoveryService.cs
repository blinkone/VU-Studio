using System.Net;
using VuStudio.Core.Providers;
using VuStudio.Sources.Remote;

namespace VuStudio.Server.Api;

public sealed record AgentDiscoveryResult(IReadOnlyList<DiscoveredAgent> Agents, string? Message);

/// <summary>
/// Caches the mDNS browse for the Sources page. The first call browses and waits (about 3 s); later calls answer from the
/// cache and refresh it in the background, at most once per <see cref="MinInterval"/>.
/// </summary>
public sealed class AgentDiscoveryService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BrowseTime = TimeSpan.FromSeconds(3);

    private readonly IAgentDiscovery _discovery;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Task<AgentDiscoveryResult>? _running;
    private AgentDiscoveryResult? _cached;
    private DateTimeOffset _lastStarted;

    public AgentDiscoveryService(IAgentDiscovery discovery, TimeProvider time)
    {
        _discovery = discovery;
        _time = time;
    }

    public async Task<AgentDiscoveryResult> GetAsync(CancellationToken cancellationToken)
    {
        Task<AgentDiscoveryResult> wait;
        lock (_gate)
        {
            if (_cached is null)
            {
                wait = _running ??= Start();
            }
            else
            {
                if (_running is null && _time.GetUtcNow() - _lastStarted >= MinInterval)
                {
                    _running = Start();
                }

                return _cached;
            }
        }

        return await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Called under the lock. The browse is not tied to the request: a client that gives up does not cancel the shared scan.
    private Task<AgentDiscoveryResult> Start()
    {
        _lastStarted = _time.GetUtcNow();
        return Task.Run(async () =>
        {
            AgentDiscoveryResult result;
            try
            {
                var agents = await _discovery.DiscoverAsync(BrowseTime).ConfigureAwait(false);
                var failure = _discovery.LastFailure;
                result = new AgentDiscoveryResult(agents, failure is null ? null : $"Network discovery is not available here ({failure}). Add the agent by its address instead.");
            }
            catch (Exception ex)
            {
                result = new AgentDiscoveryResult([], $"Network discovery failed ({ex.Message}). Add the agent by its address instead.");
            }

            lock (_gate)
            {
                // A failed refresh keeps the last good list.
                _cached = result.Message is not null && _cached is { Agents.Count: > 0 } ? _cached with { Message = result.Message } : result;
                _running = null;
            }

            return result;
        });
    }

    /// <summary>The (address, port) pairs the configured <c>agent</c> providers point at; host names are resolved when possible.</summary>
    public static async Task<HashSet<(string Address, int Port)>> AddedEndpointsAsync(IEnumerable<ProviderInstanceConfig> providers, CancellationToken cancellationToken)
    {
        var set = new HashSet<(string, int)>();
        foreach (var provider in providers)
        {
            if (!provider.Type.Equals("agent", StringComparison.Ordinal) ||
                provider.Settings["url"] is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<string>(out var url) ||
                NormalizeBase(url) is not { } uri)
            {
                continue;
            }

            var host = uri.Host.Trim('[', ']');
            set.Add((host, uri.Port));
            if (!IPAddress.TryParse(host, out _))
            {
                try
                {
                    foreach (var address in await Dns.GetHostAddressesAsync(host, cancellationToken).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
                    {
                        set.Add((address.ToString(), uri.Port));
                    }
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or TimeoutException)
                {
                    // An unresolvable name simply cannot match a discovered address.
                }
            }
        }

        return set;
    }

    private static Uri? NormalizeBase(string url)
    {
        var text = url.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }
}
