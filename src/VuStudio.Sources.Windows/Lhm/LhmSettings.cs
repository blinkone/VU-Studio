using LibreHardwareMonitor.Hardware;
using VuStudio.Core.Providers;

namespace VuStudio.Sources.Windows.Lhm;

/// <summary>Parsed settings of the <c>lhm</c> provider.</summary>
internal sealed record LhmSettings
{
    public const double MinIntervalSeconds = 0.25;
    public const double MaxIntervalSeconds = 60;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    public bool Cpu { get; init; } = true;

    public bool Gpu { get; init; } = true;

    public bool Memory { get; init; } = true;

    public bool Motherboard { get; init; } = true;

    public bool Controllers { get; init; }

    public bool Storage { get; init; } = true;

    public bool Network { get; init; } = true;

    public bool Psu { get; init; } = true;

    public bool Battery { get; init; } = true;

    /// <summary>LHM sensor type names or kinds to keep; empty means all.</summary>
    public IReadOnlySet<string> SensorTypes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static LhmSettings From(ProviderSettings s) => new()
    {
        Interval = TimeSpan.FromSeconds(Math.Clamp(s.GetDouble("interval", 1), MinIntervalSeconds, MaxIntervalSeconds)),
        Cpu = s.GetBool("cpu", true),
        Gpu = s.GetBool("gpu", true),
        Memory = s.GetBool("memory", true),
        Motherboard = s.GetBool("motherboard", true),
        Controllers = s.GetBool("controllers", false),
        Storage = s.GetBool("storage", true),
        Network = s.GetBool("network", true),
        Psu = s.GetBool("psu", true),
        Battery = s.GetBool("battery", true),
        SensorTypes = new HashSet<string>(s.GetStringList("sensorTypes"), StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>True when the sensor passes the optional "sensor types" filter.</summary>
    public bool Accepts(SensorType type, string kind) =>
        SensorTypes.Count == 0 || SensorTypes.Contains(type.ToString()) || SensorTypes.Contains(kind);
}
