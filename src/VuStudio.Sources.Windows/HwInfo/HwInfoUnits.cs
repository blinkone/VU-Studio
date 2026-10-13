using VuStudio.Core.Sensors;
using VuStudio.Sources.Windows.Common;

namespace VuStudio.Sources.Windows.HwInfo;

/// <summary>A HWiNFO unit string mapped to a canonical unit: <c>canonical = value * Factor + Offset</c>.</summary>
internal sealed record HwInfoUnit(string Unit, double Factor, double Offset = 0, string? KindOverride = null, bool IsBoolean = false)
{
    public double Convert(double value) => IsBoolean ? (value != 0 ? 1 : 0) : (value * Factor) + Offset;
}

internal static class HwInfoUnits
{
    private const double Ki = 1024;

    private static readonly Dictionary<string, HwInfoUnit> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["°C"] = new(Units.Celsius, 1),
        ["ºC"] = new(Units.Celsius, 1),
        ["C"] = new(Units.Celsius, 1),
        ["°F"] = new(Units.Celsius, 5d / 9, -32 * 5d / 9),
        ["V"] = new(Units.Volt, 1),
        ["mV"] = new(Units.Volt, 1e-3),
        ["W"] = new(Units.Watt, 1),
        ["mW"] = new(Units.Watt, 1e-3),
        ["kW"] = new(Units.Watt, 1e3),
        ["A"] = new(Units.Ampere, 1),
        ["mA"] = new(Units.Ampere, 1e-3),
        ["MHz"] = new(Units.MHz, 1),
        ["GHz"] = new(Units.MHz, 1e3),
        ["kHz"] = new(Units.MHz, 1e-3),
        ["Hz"] = new(Units.Hz, 1),
        ["%"] = new(Units.Percent, 1),
        ["RPM"] = new(Units.Rpm, 1),
        ["B"] = new(Units.Bytes, 1),
        ["KB"] = new(Units.Bytes, Ki),
        ["MB"] = new(Units.Bytes, Ki * Ki),
        ["GB"] = new(Units.Bytes, Ki * Ki * Ki),
        ["TB"] = new(Units.Bytes, Ki * Ki * Ki * Ki),
        ["B/s"] = new(Units.BytesPerSecond, 1),
        ["KB/s"] = new(Units.BytesPerSecond, Ki),
        ["MB/s"] = new(Units.BytesPerSecond, Ki * Ki),
        ["GB/s"] = new(Units.BytesPerSecond, Ki * Ki * Ki),
        ["FPS"] = new(Units.Fps, 1),
        ["ms"] = new(Units.Milliseconds, 1),
        ["µs"] = new(Units.Milliseconds, 1e-3),
        ["us"] = new(Units.Milliseconds, 1e-3),
        ["s"] = new(Units.Seconds, 1),
        ["min"] = new(Units.Seconds, 60),
        ["h"] = new(Units.Seconds, 3600),
        ["dB"] = new(Units.Decibel, 1),
        ["dBA"] = new(Units.Decibel, 1),
        ["Wh"] = new(Units.WattHour, 1),
        ["mWh"] = new(Units.WattHour, 1e-3),
        ["kWh"] = new(Units.WattHour, 1e3),
        ["L/h"] = new(Units.LitersPerHour, 1),
        ["L/min"] = new(Units.LitersPerHour, 60),
        ["x"] = new(Units.None, 1, 0, ExtraKinds.Factor),
        ["Yes/No"] = new(Units.None, 1, 0, SensorKinds.State, true),
    };

    /// <summary>
    /// Maps a HWiNFO unit string to a canonical unit. Unknown units (for example <c>T</c> for memory timings) are
    /// passed through unchanged so the reading is still available.
    /// </summary>
    public static HwInfoUnit Canonicalize(string? unit)
    {
        var u = (unit ?? "").Trim();
        if (u.Length == 0)
        {
            return new HwInfoUnit(Units.None, 1);
        }

        return Table.TryGetValue(u, out var mapped) ? mapped : new HwInfoUnit(u, 1);
    }

    /// <summary>Kind from the reading type, corrected when the unit contradicts it.</summary>
    public static string? Kind(HwInfoReadingType type, HwInfoUnit unit)
    {
        if (unit.KindOverride is { } forced)
        {
            return forced;
        }

        var fromType = type switch
        {
            HwInfoReadingType.Temp => SensorKinds.Temperature,
            HwInfoReadingType.Volt => SensorKinds.Voltage,
            HwInfoReadingType.Fan => SensorKinds.Fan,
            HwInfoReadingType.Current => SensorKinds.Current,
            HwInfoReadingType.Power => SensorKinds.Power,
            HwInfoReadingType.Clock => SensorKinds.Clock,
            HwInfoReadingType.Usage => SensorKinds.Load,
            _ => null,
        };
        var fromUnit = KindForUnit(unit.Unit);
        if (fromType is null)
        {
            return fromUnit;
        }

        if (fromUnit is null || Compatible(fromType, unit.Unit))
        {
            return fromType;
        }

        // A fan reported in percent is a duty cycle, not a speed.
        return fromType == SensorKinds.Fan && unit.Unit == Units.Percent ? SensorKinds.Level : fromUnit;
    }

    public static string? KindForUnit(string canonicalUnit) => canonicalUnit switch
    {
        Units.Celsius => SensorKinds.Temperature,
        Units.Volt => SensorKinds.Voltage,
        Units.Ampere => SensorKinds.Current,
        Units.Watt => SensorKinds.Power,
        Units.WattHour => SensorKinds.Energy,
        Units.MHz => SensorKinds.Clock,
        Units.Hz => ExtraKinds.Frequency,
        Units.Rpm => SensorKinds.Fan,
        Units.Percent => SensorKinds.Percent,
        Units.Bytes => SensorKinds.Data,
        Units.BytesPerSecond => SensorKinds.Throughput,
        Units.Fps => SensorKinds.Fps,
        Units.Milliseconds => SensorKinds.Latency,
        Units.Seconds => SensorKinds.Duration,
        Units.Decibel => ExtraKinds.Noise,
        Units.LitersPerHour => SensorKinds.Flow,
        _ => null,
    };

    private static bool Compatible(string kind, string unit) => kind switch
    {
        SensorKinds.Temperature => unit == Units.Celsius,
        SensorKinds.Voltage => unit == Units.Volt,
        SensorKinds.Fan => unit == Units.Rpm,
        SensorKinds.Current => unit == Units.Ampere,
        SensorKinds.Power => unit == Units.Watt,
        SensorKinds.Clock => unit is Units.MHz or Units.Hz,
        SensorKinds.Load => unit == Units.Percent,
        _ => true,
    };
}
