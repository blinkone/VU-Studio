using Makaretu.Dns;

namespace VuStudio.Agent;

/// <summary>
/// Advertises the agent as <c>_vustudio._tcp</c> with TXT <c>role=agent</c>, <c>v=1</c> and <c>name=…</c>, which is
/// what the VU Studio service browses for (docs/api.md, "Agent API"). Failure to advertise (no multicast on
/// the interface, Docker bridge network) is logged and never stops the agent.
/// </summary>
internal sealed class MdnsAdvertiser(AgentOptions options, ILogger<MdnsAdvertiser> logger) : IHostedService
{
    public const string ServiceType = "_vustudio._tcp";

    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;

    /// <summary>The DNS-SD profile for these options; separate from the network code so tests can inspect it.</summary>
    public static ServiceProfile CreateProfile(AgentOptions options)
    {
        var profile = new ServiceProfile(InstanceLabel(options.Name), ServiceType, (ushort)options.Port);
        profile.AddProperty("role", "agent");
        profile.AddProperty("v", "1");
        profile.AddProperty("name", options.Name);
        profile.AddProperty("version", AgentApi.Version);
        return profile;
    }

    /// <summary>A DNS label (at most 63 bytes, no dots) made from the display name.</summary>
    public static string InstanceLabel(string name)
    {
        var label = new string(name.Select(c => c == '.' || char.IsControl(c) ? '-' : c).ToArray()).Trim();
        if (label.Length == 0)
        {
            label = "vustudio-agent";
        }

        while (System.Text.Encoding.UTF8.GetByteCount(label) > 63)
        {
            label = label[..^1];
        }

        return label;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _profile = CreateProfile(options);
            _discovery = new ServiceDiscovery();
            _discovery.Advertise(_profile);
            _discovery.Announce(_profile);
            logger.LogInformation("Advertising {Service} as '{Name}' on port {Port}", ServiceType, options.Name, options.Port);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "mDNS advertisement is not available; add the agent in VU Studio by address instead");
            _discovery?.Dispose();
            _discovery = null;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_discovery is not null && _profile is not null)
            {
                _discovery.Unadvertise(_profile);
            }

            _discovery?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "mDNS goodbye failed");
        }

        _discovery = null;
        return Task.CompletedTask;
    }
}
