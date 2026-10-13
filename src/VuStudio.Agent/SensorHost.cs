using Microsoft.Extensions.Hosting;
using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.SystemMetrics;
using VuStudio.Agent.Unraid;

namespace VuStudio.Agent;

/// <summary>
/// Runs the sensor providers into a <see cref="SensorHub"/>: the shared <c>system</c> provider (Linux
/// /proc and /sys, disks, network, GPUs; the basic Windows subset elsewhere) and, when the machine
/// is an Unraid server, the <c>unraid</c> provider. Restart and backoff come from <see cref="ProviderManager"/>.
/// </summary>
internal sealed class SensorHost(AgentOptions options, ProviderManager manager, ILogger<SensorHost> logger) : IHostedService
{
    public static List<ProviderInstanceConfig> BuildConfigs(AgentOptions options, bool unraidPresent)
    {
        var configs = new List<ProviderInstanceConfig>
        {
            new()
            {
                Id = "system",
                Type = "system",
                Settings = new JsonObject
                {
                    ["interval"] = options.IntervalSeconds,
                    ["perCore"] = options.PerCore,
                    ["nvidiaSmi"] = options.NvidiaSmi,
                },
            },
        };

        if (unraidPresent)
        {
            configs.Add(new ProviderInstanceConfig { Id = UnraidProvider.ProviderId, Type = UnraidProvider.ProviderId });
        }

        return configs;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var unraid = UnraidProvider.IsPresent(options.UnraidPath);
        logger.LogInformation("Starting sensors{Unraid}", unraid ? " (Unraid detected)" : "");
        await manager.ApplyAsync(BuildConfigs(options, unraid), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => manager.StopAllAsync();
}

/// <summary>Creates <see cref="UnraidProvider"/> instances for the provider manager.</summary>
internal sealed class UnraidProviderFactory(AgentOptions options) : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = UnraidProvider.ProviderId,
        Name = new LocalizedText("Unraid state files", "File di stato di Unraid"),
        Description = new LocalizedText("Array, parity check and disk temperatures read from /var/local/emhttp.", "Array, controllo di parità e temperature dei dischi letti da /var/local/emhttp."),
        Icon = "nas",
        Platforms = PlatformSupport.Any,
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services) =>
        new UnraidProvider(
            options.UnraidPath,
            TimeSpan.FromSeconds(Math.Max(5, options.IntervalSeconds)),
            ((ILoggerFactory)services.GetService(typeof(ILoggerFactory))!).CreateLogger<UnraidProvider>());
}
