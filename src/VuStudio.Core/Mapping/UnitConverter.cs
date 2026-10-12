using System.Globalization;

namespace VuStudio.Core.Mapping;

/// <summary>
/// Conversions between canonical units (see <see cref="Sensors.Units"/>) and display units, plus
/// "auto" scaling (B → KB → MB…) for compact labels. Byte multiples use 1024, bit rates use 1000.
/// </summary>
public static class UnitConverter
{
    public const string Auto = "auto";

    private sealed record Unit(string Name, string Family, double Factor, double Offset = 0);

    private static readonly Unit[] All =
    [
        new("B", "bytes", 1),
        new("KB", "bytes", 1024),
        new("MB", "bytes", 1024d * 1024),
        new("GB", "bytes", 1024d * 1024 * 1024),
        new("TB", "bytes", 1024d * 1024 * 1024 * 1024),

        new("B/s", "rate", 1),
        new("KB/s", "rate", 1024),
        new("MB/s", "rate", 1024d * 1024),
        new("GB/s", "rate", 1024d * 1024 * 1024),
        new("bit/s", "rate", 1d / 8),
        new("Kbit/s", "rate", 1000d / 8),
        new("Mbit/s", "rate", 1000d * 1000 / 8),
        new("Gbit/s", "rate", 1000d * 1000 * 1000 / 8),

        new("°C", "temperature", 1),
        new("°F", "temperature", 5d / 9, -32 * 5d / 9),
        new("K", "temperature", 1, -273.15),

        new("Hz", "frequency", 1d / 1_000_000),
        new("kHz", "frequency", 1d / 1000),
        new("MHz", "frequency", 1),
        new("GHz", "frequency", 1000),

        new("ms", "time", 1d / 1000),
        new("s", "time", 1),
        new("min", "time", 60),
        new("h", "time", 3600),

        new("mW", "power", 1d / 1000),
        new("W", "power", 1),
        new("kW", "power", 1000),

        new("Wh", "energy", 1),
        new("kWh", "energy", 1000),

        new("mV", "voltage", 1d / 1000),
        new("V", "voltage", 1),
    ];

    private static readonly Dictionary<string, Unit> ByName = All.ToDictionary(u => u.Name, StringComparer.Ordinal);

    /// <summary>Display units compatible with <paramref name="canonicalUnit"/>, including the unit itself.</summary>
    public static IReadOnlyList<string> CompatibleUnits(string canonicalUnit) =>
        ByName.TryGetValue(canonicalUnit, out var unit)
            ? All.Where(u => u.Family == unit.Family).Select(u => u.Name).ToList()
            : [canonicalUnit];

    /// <summary>
    /// Converts <paramref name="value"/> from <paramref name="from"/> to <paramref name="to"/>.
    /// Unknown or incompatible units return the value unchanged.
    /// </summary>
    public static double Convert(double value, string from, string? to)
    {
        if (string.IsNullOrEmpty(to) || to == Auto || from == to)
        {
            return value;
        }

        if (!ByName.TryGetValue(from, out var a) || !ByName.TryGetValue(to, out var b) || a.Family != b.Family)
        {
            return value;
        }

        // value_in_base = value * factor + offset  (offset only used by temperatures)
        var baseValue = (value * a.Factor) + a.Offset;
        return (baseValue - b.Offset) / b.Factor;
    }

    public static bool AreCompatible(string a, string b) =>
        a == b || (ByName.TryGetValue(a, out var ua) && ByName.TryGetValue(b, out var ub) && ua.Family == ub.Family);

    /// <summary>
    /// Picks a unit of the same family so that <paramref name="magnitude"/> (in <paramref name="canonicalUnit"/>)
    /// reads between 1 and 1000 (1024 for bytes). Units outside known families are returned unchanged.
    /// </summary>
    public static string PickAutoUnit(double magnitude, string canonicalUnit, bool preferBits = false)
    {
        if (!ByName.TryGetValue(canonicalUnit, out var unit))
        {
            return canonicalUnit;
        }

        string[] ladder = unit.Family switch
        {
            "bytes" => ["B", "KB", "MB", "GB", "TB"],
            "rate" when preferBits => ["bit/s", "Kbit/s", "Mbit/s", "Gbit/s"],
            "rate" => ["B/s", "KB/s", "MB/s", "GB/s"],
            "frequency" => ["MHz", "GHz"],
            "power" => ["W", "kW"],
            "energy" => ["Wh", "kWh"],
            _ => [canonicalUnit],
        };

        var abs = Math.Abs(magnitude);
        var chosen = ladder[0];
        foreach (var name in ladder)
        {
            var converted = Math.Abs(Convert(abs, canonicalUnit, name));
            if (converted >= 1 || name == ladder[0])
            {
                chosen = name;
            }

            if (converted < 1000)
            {
                break;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Formats a value for display. <paramref name="decimals"/> = null chooses a sensible precision
    /// (0 decimals at ≥ 100, 1 at ≥ 10, 2 below).
    /// </summary>
    public static string FormatNumber(double value, int? decimals = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (!double.IsFinite(value))
        {
            return "–";
        }

        var d = decimals ?? Math.Abs(value) switch
        {
            >= 100 => 0,
            >= 10 => 1,
            0 => 0,
            _ => 2,
        };
        return value.ToString("F" + Math.Clamp(d, 0, 6), culture);
    }

    /// <summary>
    /// Formats <paramref name="value"/> (in <paramref name="canonicalUnit"/>) in the requested display unit
    /// ("auto" scales per value). Returns the number text and the unit label separately.
    /// </summary>
    public static (string Number, string Unit) Format(double value, string canonicalUnit, string? displayUnit = Auto, int? decimals = null, CultureInfo? culture = null)
    {
        var unit = string.IsNullOrEmpty(displayUnit) || displayUnit == Auto
            ? PickAutoUnit(value, canonicalUnit, preferBits: false)
            : displayUnit;
        var converted = Convert(value, canonicalUnit, unit);
        return (FormatNumber(converted, decimals, culture), unit);
    }
}
