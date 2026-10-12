using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Common;

/// <summary>
/// Sensible defaults for metadata the user did not spell out: the kind implied by a canonical unit,
/// the staleness window for a polling interval, and a default icon (ids from the bundled icon set).
/// </summary>
internal static class SensorHints
{
    /// <summary>The <see cref="SensorKinds"/> value usually implied by a canonical unit, or <c>null</c>.</summary>
    public static string? KindForUnit(string? unit) => unit switch
    {
        Units.Percent => SensorKinds.Percent,
        Units.Celsius => SensorKinds.Temperature,
        Units.MHz or Units.Hz => SensorKinds.Clock,
        Units.Watt => SensorKinds.Power,
        Units.Volt => SensorKinds.Voltage,
        Units.Ampere => SensorKinds.Current,
        Units.Rpm => SensorKinds.Fan,
        Units.Bytes => SensorKinds.Data,
        Units.BytesPerSecond => SensorKinds.Throughput,
        Units.Milliseconds => SensorKinds.Latency,
        Units.Seconds => SensorKinds.Duration,
        Units.Fps => SensorKinds.Fps,
        Units.Decibel => SensorKinds.Level,
        Units.WattHour => SensorKinds.Energy,
        Units.LitersPerHour => SensorKinds.Flow,
        _ => null,
    };

    /// <summary>
    /// How long a value stays valid without an update: three polling intervals, but never less than the
    /// hub default of 30 s, so slow sources (an HTTP call every 30 s) do not flicker "stale".
    /// </summary>
    public static double StaleAfterSeconds(TimeSpan interval) => Math.Max(30, Math.Ceiling(interval.TotalSeconds * 3));

    /// <summary>A default icon for a sensor, preferring what the kind says over the category.</summary>
    public static string Icon(SensorCategory category, string? kind) => kind switch
    {
        SensorKinds.Temperature => "thermometer",
        SensorKinds.Fan => "fan",
        SensorKinds.Power => "flash",
        SensorKinds.Voltage => "lightning-bolt",
        SensorKinds.Current => "flash",
        SensorKinds.Latency => "signal",
        SensorKinds.Duration => "timer-outline",
        SensorKinds.Humidity => "water-percent",
        SensorKinds.Energy => "battery",
        SensorKinds.Count => "counter",
        _ => category switch
        {
            SensorCategory.Cpu => "cpu-64-bit",
            SensorCategory.Gpu => "expansion-card",
            SensorCategory.Memory => "memory",
            SensorCategory.Storage => "harddisk",
            SensorCategory.Network => "lan",
            SensorCategory.Cooling => "fan",
            SensorCategory.Power => "flash",
            SensorCategory.Battery => "battery",
            SensorCategory.Motherboard => "chip",
            SensorCategory.Audio => "waveform",
            SensorCategory.Media => "music",
            SensorCategory.Game => "gamepad-variant",
            SensorCategory.Process => "cog",
            SensorCategory.Time => "clock-outline",
            SensorCategory.Weather => "weather-partly-cloudy",
            SensorCategory.Finance => "chart-line",
            SensorCategory.Home => "home",
            SensorCategory.System => "desktop-tower",
            _ => "pulse",
        },
    };
}
