using System.Net;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Pings;

/// <summary>
/// Pings a list of hosts, and optionally the default gateway, in parallel on an interval and publishes
/// latency, packet loss over the last N pings, and reachability for each.
/// </summary>
/// <remarks>
/// Sensor ids, per target: <c>{id}/rtt</c> (ms, only while it answers), <c>{id}/loss</c> (%), <c>{id}/up</c>
/// (1 when the latest ping was answered). The gateway is <c>gateway/…</c>, and <c>internet/up</c> is 1 while
/// any target other than the gateway is up. Without a default gateway the gateway counts as down.
/// </remarks>
internal sealed class PingProvider : PollingSensorProvider
{
    private readonly PingSettings _settings;
    private readonly IPinger _pinger;
    private readonly IGatewayLocator _gateways;
    private readonly List<Monitored> _monitored = [];
    private readonly Monitored? _gateway;
    private readonly double _staleAfter;
    private SensorRegistry? _sensors;

    public PingProvider(string id, PingSettings settings, IPinger pinger, IGatewayLocator gateways, ILogger logger)
        : base(id, settings.Interval, logger)
    {
        _settings = settings;
        _pinger = pinger;
        _gateways = gateways;
        _staleAfter = SensorHints.StaleAfterSeconds(settings.Interval);

        foreach (var target in settings.Targets)
        {
            _monitored.Add(new Monitored(target.Id, target.Name, target.Host, settings.Window));
        }

        if (settings.IncludeGateway)
        {
            _gateway = new Monitored(PingSettings.GatewayId, "Gateway", null, settings.Window);
            _monitored.Add(_gateway);
        }
    }

    protected override Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sensors = new SensorRegistry(sink, _staleAfter);
        foreach (var target in _monitored)
        {
            DefineTarget(target);
        }

        if (_monitored.Any(m => m != _gateway))
        {
            _sensors.Define(UpInfo("internet/up", "Internet reachable", "Internet", "earth"));
        }

        return Task.CompletedTask;
    }

    protected override async Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var sensors = _sensors ?? throw new InvalidOperationException("The provider was not initialised.");
        if (_monitored.Count == 0)
        {
            throw new InvalidOperationException(_settings.Issues.Count > 0
                ? string.Join(" ", _settings.Issues)
                : "No ping targets are configured.");
        }

        if (_gateway is not null)
        {
            TrackGateway(_gateways.FindDefaultGateway());
        }

        // All hosts at once: a dead one must not delay the others by its timeout.
        var outcomes = await Task.WhenAll(_monitored.Select(m => ProbeAsync(m, cancellationToken))).ConfigureAwait(false);

        var internetUp = false;
        var attempted = 0;
        string? firstError = null;
        var errored = 0;
        for (var i = 0; i < _monitored.Count; i++)
        {
            var target = _monitored[i];
            var outcome = outcomes[i];
            target.Window.Add(outcome.Success);
            if (target.Host is not null)
            {
                attempted++;
                if (outcome.Error is not null)
                {
                    errored++;
                    firstError ??= outcome.Error;
                }
            }


            if (outcome.Success)
            {
                sensors.Publish($"{target.Id}/rtt", outcome.RoundTripMs);
            }
            else
            {
                sensors.Invalidate($"{target.Id}/rtt");
            }

            sensors.Publish($"{target.Id}/loss", target.Window.LossPercent);
            sensors.Publish($"{target.Id}/up", outcome.Success ? 1 : 0);
            internetUp |= outcome.Success && target != _gateway;
        }

        if (sensors.IsDefined("internet/up"))
        {
            sensors.Publish("internet/up", internetUp ? 1 : 0);
        }

        // Unreachable hosts are normal. When every ping that was tried could not even be sent (no permission to
        // use ICMP, unknown host name), something is wrong with the setup and the provider says so.
        if (attempted > 0 && errored == attempted)
        {
            throw new InvalidOperationException("Ping failed: " + firstError);
        }
    }

    private Task<PingOutcome> ProbeAsync(Monitored target, CancellationToken cancellationToken) =>
        target.Host is null
            ? Task.FromResult(PingOutcome.NoReply()) // no default gateway at the moment
            : _pinger.PingAsync(target.Host, _settings.Timeout, cancellationToken);

    /// <summary>Follows the gateway address: a different gateway is a different network, so its history restarts.</summary>
    private void TrackGateway(IPAddress? address)
    {
        var gateway = _gateway!;
        var host = address?.ToString();
        if (host == gateway.Host)
        {
            return;
        }

        gateway.Host = host;
        gateway.Name = host is null ? "Gateway" : $"Gateway ({host})";
        gateway.Window.Reset();
        DefineTarget(gateway);
    }

    private void DefineTarget(Monitored target)
    {
        var sensors = _sensors!;
        var isGateway = target == _gateway;
        var timeoutMs = _settings.Timeout.TotalMilliseconds;

        sensors.Define(new SensorInfo
        {
            Id = $"{target.Id}/rtt",
            Name = "Latency",
            Device = target.Name,
            Category = SensorCategory.Network,
            Kind = SensorKinds.Latency,
            Unit = Units.Milliseconds,
            Min = 0,
            Max = timeoutMs,
            Icon = isGateway ? "router-network" : "signal",
        });
        sensors.Define(new SensorInfo
        {
            Id = $"{target.Id}/loss",
            Name = "Packet loss",
            Device = target.Name,
            Category = SensorCategory.Network,
            Kind = SensorKinds.Percent,
            Unit = Units.Percent,
            Min = 0,
            Max = 100,
            Icon = "alert-circle",
        });
        sensors.Define(UpInfo($"{target.Id}/up", "Reachable", target.Name, isGateway ? "router-network" : "lan-connect"));
    }

    private static SensorInfo UpInfo(string id, string name, string device, string icon) => new()
    {
        Id = id,
        Name = name,
        Device = device,
        Category = SensorCategory.Network,
        Kind = SensorKinds.State,
        Min = 0,
        Max = 1,
        Icon = icon,
    };

    /// <summary>A target and its running statistics.</summary>
    private sealed class Monitored(string id, string name, string? host, int window)
    {
        public string Id { get; } = id;

        public string Name { get; set; } = name;

        public string? Host { get; set; } = host;

        public PingWindow Window { get; } = new(window);
    }
}
