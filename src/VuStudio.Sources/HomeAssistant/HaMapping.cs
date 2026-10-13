using System.Globalization;
using System.Text.Json.Nodes;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;

namespace VuStudio.Sources.HomeAssistant;

/// <summary>How a Home Assistant style unit and device class map onto a canonical unit.</summary>
internal sealed record HaUnit(string Unit, double Scale, double Offset, SensorCategory Category, string? Kind, double? Min, double? Max, string? Icon)
{
    public double? Convert(double value) => value * Scale + Offset;
}

/// <summary>
/// Maps <c>unit_of_measurement</c> and <c>device_class</c> (shared by Home Assistant states and MQTT discovery)
/// to canonical units, categories and kinds, and turns state strings into numbers.
/// </summary>
internal static partial class HaMapping
{
    private static readonly string[] OutdoorWords = ["outdoor", "outside", "exterior", "esterno", "esterna", "weather", "meteo", "garden", "giardino"];

    /// <summary>Maps a unit (and, when the unit is missing, the device class) to a canonical unit.</summary>
    public static HaUnit Map(string? unit, string? deviceClass, string? entityId = null, string? name = null)
    {
        var u = unit?.Trim();
        var dc = deviceClass?.Trim().ToLowerInvariant();
        var outdoor = IsOutdoor(entityId, name) || dc is "wind_speed" or "precipitation" or "precipitation_intensity" or "atmospheric_pressure" or "irradiance";

        var mapped = u switch
        {
            "°C" or "℃" or "C" => Make(Units.Celsius, 1, 0, TemperatureCategory(outdoor), SensorKinds.Temperature, -20, 50),
            "°F" or "℉" or "F" => Make(Units.Celsius, 5d / 9, -160d / 9, TemperatureCategory(outdoor), SensorKinds.Temperature, -20, 50),
            "K" => Make(Units.Celsius, 1, -273.15, TemperatureCategory(outdoor), SensorKinds.Temperature, -20, 50),
            "%" => PercentOf(dc),
            "W" => Make(Units.Watt, 1, 0, SensorCategory.Power, SensorKinds.Power),
            "kW" => Make(Units.Watt, 1000, 0, SensorCategory.Power, SensorKinds.Power),
            "mW" => Make(Units.Watt, 0.001, 0, SensorCategory.Power, SensorKinds.Power),
            "MW" => Make(Units.Watt, 1_000_000, 0, SensorCategory.Power, SensorKinds.Power),
            "Wh" => Make(Units.WattHour, 1, 0, SensorCategory.Power, SensorKinds.Energy),
            "kWh" => Make(Units.WattHour, 1000, 0, SensorCategory.Power, SensorKinds.Energy),
            "MWh" => Make(Units.WattHour, 1_000_000, 0, SensorCategory.Power, SensorKinds.Energy),
            "V" => Make(Units.Volt, 1, 0, SensorCategory.Power, SensorKinds.Voltage),
            "mV" => Make(Units.Volt, 0.001, 0, SensorCategory.Power, SensorKinds.Voltage),
            "A" => Make(Units.Ampere, 1, 0, SensorCategory.Power, SensorKinds.Current),
            "mA" => Make(Units.Ampere, 0.001, 0, SensorCategory.Power, SensorKinds.Current),
            "Hz" => Make(Units.Hz, 1, 0, SensorCategory.Power, SensorKinds.Clock),
            "kHz" => Make(Units.Hz, 1000, 0, SensorCategory.System, SensorKinds.Clock),
            "MHz" => Make(Units.MHz, 1, 0, SensorCategory.System, SensorKinds.Clock),
            "GHz" => Make(Units.MHz, 1000, 0, SensorCategory.System, SensorKinds.Clock),
            "B" => Make(Units.Bytes, 1, 0, SensorCategory.Storage, SensorKinds.Data),
            "kB" or "KB" => Make(Units.Bytes, 1e3, 0, SensorCategory.Storage, SensorKinds.Data),
            "KiB" => Make(Units.Bytes, 1024, 0, SensorCategory.Storage, SensorKinds.Data),
            "MB" => Make(Units.Bytes, 1e6, 0, SensorCategory.Storage, SensorKinds.Data),
            "MiB" => Make(Units.Bytes, 1024d * 1024, 0, SensorCategory.Storage, SensorKinds.Data),
            "GB" => Make(Units.Bytes, 1e9, 0, SensorCategory.Storage, SensorKinds.Data),
            "GiB" => Make(Units.Bytes, 1024d * 1024 * 1024, 0, SensorCategory.Storage, SensorKinds.Data),
            "TB" => Make(Units.Bytes, 1e12, 0, SensorCategory.Storage, SensorKinds.Data),
            "TiB" => Make(Units.Bytes, 1024d * 1024 * 1024 * 1024, 0, SensorCategory.Storage, SensorKinds.Data),
            "B/s" => Make(Units.BytesPerSecond, 1, 0, SensorCategory.Network, SensorKinds.Throughput),
            "kB/s" => Make(Units.BytesPerSecond, 1e3, 0, SensorCategory.Network, SensorKinds.Throughput),
            "MB/s" => Make(Units.BytesPerSecond, 1e6, 0, SensorCategory.Network, SensorKinds.Throughput),
            "bit/s" => Make(Units.BytesPerSecond, 1d / 8, 0, SensorCategory.Network, SensorKinds.Throughput),
            "kbit/s" or "kbps" => Make(Units.BytesPerSecond, 125, 0, SensorCategory.Network, SensorKinds.Throughput),
            "Mbit/s" or "Mbps" => Make(Units.BytesPerSecond, 125_000, 0, SensorCategory.Network, SensorKinds.Throughput),
            "Gbit/s" or "Gbps" => Make(Units.BytesPerSecond, 125_000_000, 0, SensorCategory.Network, SensorKinds.Throughput),
            "ms" => Make(Units.Milliseconds, 1, 0, SensorCategory.Network, SensorKinds.Latency),
            "s" => Make(Units.Seconds, 1, 0, SensorCategory.Time, SensorKinds.Duration),
            "min" => Make(Units.Seconds, 60, 0, SensorCategory.Time, SensorKinds.Duration),
            "h" => Make(Units.Seconds, 3600, 0, SensorCategory.Time, SensorKinds.Duration),
            "d" => Make(Units.Seconds, 86400, 0, SensorCategory.Time, SensorKinds.Duration),
            "rpm" or "RPM" => Make(Units.Rpm, 1, 0, SensorCategory.Cooling, SensorKinds.Fan),
            "dB" or "dBA" => Make(Units.Decibel, 1, 0, SensorCategory.Audio, SensorKinds.Level),
            "L/h" => Make(Units.LitersPerHour, 1, 0, SensorCategory.Home, SensorKinds.Flow),
            "hPa" or "mbar" or "mmHg" or "kPa" or "Pa" => Make(u!, 1, 0, outdoor ? SensorCategory.Weather : SensorCategory.Home, SensorKinds.Level),
            "km/h" or "m/s" or "mph" or "kn" or "mm" or "mm/h" or "in" or "lx" or "ppm" or "ppb" or "µg/m³" or "W/m²" or "UV index" =>
                Make(u!, 1, 0, outdoor || dc is "wind_speed" or "precipitation" ? SensorCategory.Weather : SensorCategory.Home, null),
            _ => null,
        };
        if (mapped is not null)
        {
            return Decorate(mapped);
        }

        // No (known) unit: let the device class decide the category, keep the unit text for display.
        var fallback = dc switch
        {
            "temperature" => Make(Units.Celsius, 1, 0, TemperatureCategory(outdoor), SensorKinds.Temperature, -20, 50),
            "humidity" or "moisture" => Make(Units.Percent, 1, 0, outdoor ? SensorCategory.Weather : SensorCategory.Home, SensorKinds.Humidity, 0, 100),
            "battery" => Make(Units.Percent, 1, 0, SensorCategory.Battery, SensorKinds.Level, 0, 100),
            "power" or "apparent_power" or "reactive_power" => Make(Units.Watt, 1, 0, SensorCategory.Power, SensorKinds.Power),
            "energy" => Make(Units.WattHour, 1, 0, SensorCategory.Power, SensorKinds.Energy),
            "voltage" => Make(Units.Volt, 1, 0, SensorCategory.Power, SensorKinds.Voltage),
            "current" => Make(Units.Ampere, 1, 0, SensorCategory.Power, SensorKinds.Current),
            "power_factor" => Make(Units.None, 1, 0, SensorCategory.Power, SensorKinds.Level, 0, 1),
            "frequency" => Make(Units.Hz, 1, 0, SensorCategory.Power, SensorKinds.Clock),
            "signal_strength" => Make(Units.None, 1, 0, SensorCategory.Network, SensorKinds.Level),
            "duration" => Make(Units.Seconds, 1, 0, SensorCategory.Time, SensorKinds.Duration),
            _ => null,
        };
        return Decorate(fallback ?? new HaUnit(u ?? Units.None, 1, 0, Home(entityId, dc), null, null, null, null));
    }

    private static SensorCategory Home(string? entityId, string? deviceClass) =>
        entityId?.StartsWith("weather.", StringComparison.Ordinal) == true ? SensorCategory.Weather
        : deviceClass is "pressure" or "atmospheric_pressure" or "wind_speed" or "precipitation" or "uv_index" ? SensorCategory.Weather
        : SensorCategory.Home;

    private static SensorCategory TemperatureCategory(bool outdoor) => outdoor ? SensorCategory.Weather : SensorCategory.Home;

    private static HaUnit PercentOf(string? deviceClass) => deviceClass switch
    {
        "battery" => Make(Units.Percent, 1, 0, SensorCategory.Battery, SensorKinds.Level, 0, 100),
        "humidity" or "moisture" => Make(Units.Percent, 1, 0, SensorCategory.Home, SensorKinds.Humidity, 0, 100),
        _ => Make(Units.Percent, 1, 0, SensorCategory.Home, SensorKinds.Percent, 0, 100),
    };

    private static HaUnit Make(string unit, double scale, double offset, SensorCategory category, string? kind, double? min = null, double? max = null) =>
        new(unit, scale, offset, category, kind, min, max, null);

    private static HaUnit Decorate(HaUnit unit) => unit with { Icon = SensorHints.Icon(unit.Category, unit.Kind) };

    private static bool IsOutdoor(string? entityId, string? name)
    {
        var text = ((entityId ?? "") + " " + (name ?? "")).ToLowerInvariant();
        return OutdoorWords.Any(w => text.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Words Home Assistant, Tasmota, Zigbee2MQTT and Shelly use for on/off.</summary>
    public static double? BoolWord(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "online" or "open" or "opened" or "home" or "locked" or "detected" or "playing" or "heat" => 1,
        "off" or "false" or "no" or "offline" or "closed" or "not_home" or "unlocked" or "clear" or "idle" or "paused" or "standby" => 0,
        _ => null,
    };

    /// <summary>States that mean "no value".</summary>
    public static bool IsNoValue(string? state) => string.IsNullOrEmpty(state) || state is "unavailable" or "unknown" or "none" or "None" or "null";

    /// <summary>The number in a state string: plain numbers (dot decimal, as Home Assistant always writes them) or, optionally, on/off words.</summary>
    public static double? ToNumber(string? state, bool allowWords)
    {
        if (IsNoValue(state))
        {
            return null;
        }

        if (double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            return number;
        }

        return allowWords ? BoolWord(state) : null;
    }

    /// <summary>
    /// Reads a simple Jinja value template (<c>{{ value_json.temperature }}</c>, <c>{{ value_json['a']['b'] }}</c>)
    /// as a JSONPath-lite path. Anything more complex returns <c>null</c>.
    /// </summary>
    public static string? PathFromTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var t = template.Trim();
        if (!t.StartsWith("{{", StringComparison.Ordinal) || !t.EndsWith("}}", StringComparison.Ordinal))
        {
            return null;
        }

        var inner = t[2..^2].Trim();
        if (inner == "value")
        {
            return null;
        }

        if (!inner.StartsWith("value_json", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = inner["value_json".Length..].Trim();
        if (!SimplePath().IsMatch(rest))
        {
            return null;
        }

        var path = "$" + rest;
        return JsonPathLite.TryParse(path, out _) ? path : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:\.[A-Za-z_]\w*|\[\s*'[^']+'\s*\]|\[\s*""[^""]+""\s*\]|\[\s*-?\d+\s*\])+$")]
    private static partial System.Text.RegularExpressions.Regex SimplePath();

    /// <summary>The first string of a possibly abbreviated key (<c>stat_t</c> / <c>state_topic</c>).</summary>
    public static string? Key(JsonNode? config, params string[] names)
    {
        foreach (var name in names)
        {
            if (JsonRead.Str(config, name) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }
}
