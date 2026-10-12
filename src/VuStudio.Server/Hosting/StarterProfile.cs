using VuStudio.Core.Config;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Core.Profiles;
using VuStudio.Core.Sensors;

namespace VuStudio.Server.Hosting;

/// <summary>
/// The profile created on first run: CPU, GPU, RAM and CPU temperature on four dials, each in a
/// different theme family so the user immediately sees what is possible. Pages start unbound and
/// <see cref="StarterBinder"/> attaches the best matching sensors once providers report them.
/// </summary>
public static class StarterProfile
{
    public const string CpuLoad = "CPU";
    public const string GpuLoad = "GPU";
    public const string MemoryLoad = "RAM";
    public const string CpuTemperature = "CPU °C";

    private static readonly (string Label, string Theme, string Icon)[] Pages =
    [
        (CpuLoad, "vintage/classic", "cpu-64-bit"),
        (GpuLoad, "instrument/gauge", "expansion-card"),
        (MemoryLoad, "minimal/arc", "memory"),
        (CpuTemperature, "hud/frame", "thermometer"),
    ];

    public static Profile Create() => new()
    {
        Id = "default",
        Name = "Default",
        Icon = "gauge",
        Activation = ProfileActivation.Always,
        Priority = 0,
        Dials = Pages.Select((p, i) => new DialAssignment
        {
            Slot = i + 1,
            Pages =
            [
                new DialPage
                {
                    Id = $"starter-{i + 1}",
                    Value = new ValueBinding { Label = p.Label },
                    Face = new FaceConfig { Theme = p.Theme, Icon = p.Icon },
                    Light = new BacklightConfig { Mode = BacklightMode.Gradient, Brightness = 70 },
                },
            ],
        }).ToList(),
    };

    /// <summary>Scores how well <paramref name="info"/> fits a starter label; 0 = not a candidate.</summary>
    public static int Score(string label, SensorInfo info)
    {
        var name = info.Name.ToLowerInvariant();
        var id = info.Id.ToLowerInvariant();
        var remote = info.Host is not null ? -50 : 0;
        return label switch
        {
            CpuLoad when info.Category == SensorCategory.Cpu && info.Kind == SensorKinds.Load =>
                100 + remote + (name.Contains("total") ? 50 : 0) + (id.EndsWith("cpu/load", StringComparison.Ordinal) ? 40 : 0) - (name.Contains("core #") || name.Contains("thread") ? 60 : 0),
            GpuLoad when info.Category == SensorCategory.Gpu && info.Kind == SensorKinds.Load =>
                100 + remote + (name.Contains("core") ? 50 : 0) + (name.Contains("3d") ? 20 : 0) - (name.Contains("memory") || name.Contains("video") || name.Contains("bus") ? 40 : 0),
            MemoryLoad when info.Category == SensorCategory.Memory && info.Kind == SensorKinds.Load =>
                100 + remote + (name.Contains("virtual") ? -40 : 0) + (name is "memory" or "memory load" ? 30 : 0),
            CpuTemperature when info.Category == SensorCategory.Cpu && info.Kind == SensorKinds.Temperature =>
                100 + remote + (name.Contains("package") ? 50 : 0) + (name.Contains("tctl") || name.Contains("tdie") ? 45 : 0) - (name.Contains("core #") || name.Contains("distance") ? 40 : 0),
            _ => 0,
        };
    }

    public static bool IsStarterLabel(string? label) => label is CpuLoad or GpuLoad or MemoryLoad or CpuTemperature;
}

/// <summary>Binds unbound starter pages to the best matching sensors during the first minutes after start.</summary>
public sealed class StarterBinder(ConfigStore config, SensorHub hub, ILogger<StarterBinder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline && await timer.WaitForNextTickAsync(stoppingToken))
        {
            var pending = config.Current.Profiles
                .SelectMany(p => p.Dials)
                .SelectMany(d => d.Pages)
                .Where(p => p.Value.SensorId is null && StarterProfile.IsStarterLabel(p.Value.Label))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            var catalog = hub.GetCatalog();
            var matches = new Dictionary<string, string>();
            foreach (var label in pending.Select(p => p.Value.Label!).Distinct())
            {
                var best = catalog
                    .Select(info => (info, score: StarterProfile.Score(label, info)))
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .FirstOrDefault();
                if (best.info is not null)
                {
                    matches[label] = best.info.Id;
                }
            }

            if (matches.Count == 0)
            {
                continue;
            }

            config.Update(c =>
            {
                foreach (var page in c.Profiles.SelectMany(p => p.Dials).SelectMany(d => d.Pages))
                {
                    if (page.Value.SensorId is null && page.Value.Label is { } label && matches.TryGetValue(label, out var sensorId))
                    {
                        page.Value.SensorId = sensorId;
                        logger.LogInformation("Starter page '{Label}' bound to {Sensor}", label, sensorId);
                    }
                }
            });
        }
    }
}
