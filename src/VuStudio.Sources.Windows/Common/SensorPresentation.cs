using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Windows.Common;

/// <summary>Sensor kinds used by the Windows providers that <see cref="SensorKinds"/> does not define yet.</summary>
internal static class ExtraKinds
{
    public const string Factor = "factor";
    public const string Noise = "noise";
    public const string Frequency = "frequency";
}

/// <summary>Default gauge ranges and icons shared by the Windows providers. Pure functions.</summary>
internal static class SensorPresentation
{
    public static (double? Min, double? Max) Range(string? kind, SensorCategory category) => kind switch
    {
        SensorKinds.Load or SensorKinds.Percent or SensorKinds.Level or SensorKinds.Humidity => (0, 100),
        SensorKinds.Temperature => category switch
        {
            SensorCategory.Cpu or SensorCategory.Gpu => (20, 100),
            SensorCategory.Storage => (20, 70),
            _ => (0, 100),
        },
        SensorKinds.Clock => (0, 6000),
        SensorKinds.Power => category switch
        {
            SensorCategory.Cpu => (0, 250),
            SensorCategory.Gpu => (0, 450),
            _ => (null, null),
        },
        SensorKinds.Fan => (0, 3000),
        _ => (null, null),
    };

    /// <summary>Icon id from the bundled icon set.</summary>
    public static string Icon(string? kind, SensorCategory category)
    {
        switch (kind)
        {
            case SensorKinds.Temperature: return "thermometer";
            case SensorKinds.Fan: return "fan";
            case SensorKinds.Flow: return "water-pump";
            case ExtraKinds.Noise: return "volume-high";
            case SensorKinds.Humidity: return "water-percent";
            case SensorKinds.Voltage:
            case SensorKinds.Current:
            case SensorKinds.Energy: return "lightning-bolt";
            case SensorKinds.Power: return category == SensorCategory.Battery ? "battery" : "flash";
            case SensorKinds.Throughput: return "swap-vertical";
            case SensorKinds.Duration: return "timer-outline";
            case SensorKinds.Fps: return "monitor-dashboard";
            case SensorKinds.Latency: return "av-timer";
            case SensorKinds.Clock: return "speedometer";
            case SensorKinds.State: return "check-circle";
        }

        return CategoryIcon(category);
    }

    public static string CategoryIcon(SensorCategory category) => category switch
    {
        SensorCategory.Cpu => "cpu-64-bit",
        SensorCategory.Gpu => "expansion-card",
        SensorCategory.Memory => "memory",
        SensorCategory.Storage => "harddisk",
        SensorCategory.Network => "ethernet",
        SensorCategory.Cooling => "fan",
        SensorCategory.Power => "flash",
        SensorCategory.Battery => "battery",
        SensorCategory.Motherboard => "chip",
        _ => "desktop-tower",
    };

    /// <summary>All icon ids this class can return; used by tests to check them against the icon set.</summary>
    internal static IEnumerable<string> AllIcons()
    {
        string?[] kinds =
        [
            null, SensorKinds.Temperature, SensorKinds.Fan, SensorKinds.Flow, ExtraKinds.Noise, SensorKinds.Humidity,
            SensorKinds.Voltage, SensorKinds.Current, SensorKinds.Energy, SensorKinds.Power, SensorKinds.Throughput,
            SensorKinds.Duration, SensorKinds.Fps, SensorKinds.Latency, SensorKinds.Clock, SensorKinds.State,
        ];
        return Enum.GetValues<SensorCategory>()
            .SelectMany(c => kinds.Select(k => Icon(k, c)))
            .Distinct(StringComparer.Ordinal);
    }
}
