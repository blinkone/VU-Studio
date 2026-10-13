using LibreHardwareMonitor.Hardware;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Windows.Common;

namespace VuStudio.Sources.Windows.Lhm;

/// <summary>How one LibreHardwareMonitor sensor type is published: kind, canonical unit and value scale.</summary>
internal sealed record LhmSensorSpec(string Kind, string Unit, double Factor)
{
    public double Convert(double value) => value * Factor;
}

/// <summary>
/// Pure mapping tables from LibreHardwareMonitor enums to VU Studio metadata.
/// Source units are the ones documented on <c>SensorType</c> in LibreHardwareMonitorLib 0.9.6.
/// </summary>
internal static class LhmMapping
{
    private const double Kibi = 1024d;

    /// <summary>
    /// Returns the publishing spec of a sensor type, or <c>null</c> when it is not published
    /// (<c>Conductivity</c> has no canonical unit yet).
    /// </summary>
    public static LhmSensorSpec? Describe(SensorType type) => type switch
    {
        SensorType.Voltage => new(SensorKinds.Voltage, Units.Volt, 1),
        SensorType.Current => new(SensorKinds.Current, Units.Ampere, 1),
        SensorType.Power => new(SensorKinds.Power, Units.Watt, 1),
        SensorType.Clock => new(SensorKinds.Clock, Units.MHz, 1),
        SensorType.Temperature => new(SensorKinds.Temperature, Units.Celsius, 1),
        SensorType.Load => new(SensorKinds.Load, Units.Percent, 1),
        SensorType.Frequency => new(ExtraKinds.Frequency, Units.Hz, 1),
        SensorType.Fan => new(SensorKinds.Fan, Units.Rpm, 1),
        SensorType.Flow => new(SensorKinds.Flow, Units.LitersPerHour, 1),
        SensorType.Control => new(SensorKinds.Level, Units.Percent, 1),
        SensorType.Level => new(SensorKinds.Level, Units.Percent, 1),
        SensorType.Factor => new(ExtraKinds.Factor, Units.None, 1),
        // GB = 2^30 bytes and MB = 2^20 bytes in LHM, not 10^9 / 10^6.
        SensorType.Data => new(SensorKinds.Data, Units.Bytes, Kibi * Kibi * Kibi),
        SensorType.SmallData => new(SensorKinds.Data, Units.Bytes, Kibi * Kibi),
        SensorType.Throughput => new(SensorKinds.Throughput, Units.BytesPerSecond, 1),
        SensorType.TimeSpan => new(SensorKinds.Duration, Units.Seconds, 1),
        SensorType.Timing => new(SensorKinds.Latency, Units.Milliseconds, 1e-6), // ns -> ms
        SensorType.Energy => new(SensorKinds.Energy, Units.WattHour, 1e-3), // mWh -> Wh
        SensorType.Noise => new(ExtraKinds.Noise, Units.Decibel, 1), // dBA
        SensorType.Humidity => new(SensorKinds.Humidity, Units.Percent, 1),
        _ => null,
    };

    public static SensorCategory Category(HardwareType type) => type switch
    {
        HardwareType.Cpu => SensorCategory.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SensorCategory.Gpu,
        HardwareType.Memory => SensorCategory.Memory,
        HardwareType.Motherboard or HardwareType.SuperIO => SensorCategory.Motherboard,
        HardwareType.Storage => SensorCategory.Storage,
        HardwareType.Network => SensorCategory.Network,
        HardwareType.Cooler or HardwareType.EmbeddedController => SensorCategory.Cooling,
        HardwareType.Psu or HardwareType.PowerMonitor => SensorCategory.Power,
        HardwareType.Battery => SensorCategory.Battery,
        _ => SensorCategory.System,
    };

    /// <summary>LHM identifier without its leading slash, e.g. <c>amdcpu/0/temperature/2</c>.</summary>
    public static string LocalId(string identifier) => identifier.TrimStart('/');
}
