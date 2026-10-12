using VuStudio.Core.Config;
using VuStudio.Core.Mapping;
using VuStudio.Core.Profiles;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.Engine;

/// <summary>A value binding resolved against the current sensor metadata.</summary>
/// <param name="CanonicalUnit">Unit the sensor publishes in.</param>
/// <param name="DisplayUnit">Unit used for the mapping and labels.</param>
/// <param name="Mapping">Mapping in display units.</param>
public sealed record ResolvedBinding(SensorInfo? Sensor, string CanonicalUnit, string DisplayUnit, ValueMapping Mapping, int? Decimals)
{
    public double ToDisplay(double canonical) => UnitConverter.Convert(canonical, CanonicalUnit, DisplayUnit);
}

/// <summary>Turns a <see cref="ValueBinding"/> into a concrete unit and mapping.</summary>
public static class BindingResolver
{
    public static ResolvedBinding Resolve(ValueBinding binding, SensorInfo? sensor, GeneralSettings general)
    {
        var canonical = sensor?.Unit ?? Units.Percent;
        var display = ChooseDisplayUnit(binding, sensor, general);

        double? suggestedMin = sensor?.Min is { } smin ? UnitConverter.Convert(smin, canonical, display) : null;
        double? suggestedMax = sensor?.Max is { } smax ? UnitConverter.Convert(smax, canonical, display) : null;

        var min = binding.Min ?? suggestedMin ?? 0;
        var max = binding.Max ?? suggestedMax ?? DefaultMax(canonical, display);
        if (Math.Abs(max - min) < 1e-12)
        {
            max = min + 1;
        }

        var mapping = new ValueMapping(min, max, binding.Scale, binding.Points, binding.Invert);
        return new ResolvedBinding(sensor, canonical, display, mapping, binding.Decimals);
    }

    private static string ChooseDisplayUnit(ValueBinding binding, SensorInfo? sensor, GeneralSettings general)
    {
        var canonical = sensor?.Unit ?? Units.Percent;
        if (!string.IsNullOrEmpty(binding.DisplayUnit) && binding.DisplayUnit != UnitConverter.Auto &&
            UnitConverter.AreCompatible(canonical, binding.DisplayUnit))
        {
            return binding.DisplayUnit;
        }

        switch (canonical)
        {
            case Units.Celsius:
                return general.TemperatureUnit is "°F" ? "°F" : Units.Celsius;
            case Units.BytesPerSecond:
            {
                var magnitude = binding.Max ?? sensor?.Max ?? 125_000_000; // ~1 Gbit/s
                return UnitConverter.PickAutoUnit(magnitude, canonical, preferBits: general.NetworkInBits && sensor?.Category == SensorCategory.Network);
            }

            case Units.Bytes:
                return UnitConverter.PickAutoUnit(binding.Max ?? sensor?.Max ?? 1024d * 1024 * 1024, canonical);
            default:
                return canonical;
        }
    }

    private static double DefaultMax(string canonical, string display) => canonical switch
    {
        Units.Celsius => display == "°F" ? 212 : 100,
        Units.Rpm => 3000,
        Units.Watt => 300,
        Units.MHz => 5000,
        Units.Milliseconds => 200,
        Units.Fps => 165,
        Units.BytesPerSecond => UnitConverter.Convert(125_000_000, Units.BytesPerSecond, display),
        _ => 100,
    };
}
