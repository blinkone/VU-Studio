namespace VuStudio.Sources.Online;

/// <summary>WMO weather interpretation codes as emitted by Open-Meteo, in English and Italian.</summary>
public static class WmoCodes
{
    private static readonly Dictionary<int, (string En, string It, string Icon)> Table = new()
    {
        [0] = ("Clear sky", "Sereno", "weather-sunny"),
        [1] = ("Mainly clear", "Prevalentemente sereno", "weather-sunny"),
        [2] = ("Partly cloudy", "Parzialmente nuvoloso", "weather-partly-cloudy"),
        [3] = ("Overcast", "Coperto", "weather-cloudy"),
        [45] = ("Fog", "Nebbia", "weather-fog"),
        [48] = ("Depositing rime fog", "Nebbia con brina", "weather-fog"),
        [51] = ("Drizzle, light", "Pioggerella debole", "weather-rainy"),
        [53] = ("Drizzle, moderate", "Pioggerella moderata", "weather-rainy"),
        [55] = ("Drizzle, dense", "Pioggerella intensa", "weather-rainy"),
        [56] = ("Freezing drizzle, light", "Pioggerella gelata debole", "weather-rainy"),
        [57] = ("Freezing drizzle, dense", "Pioggerella gelata intensa", "weather-rainy"),
        [61] = ("Rain, slight", "Pioggia debole", "weather-rainy"),
        [63] = ("Rain, moderate", "Pioggia moderata", "weather-rainy"),
        [65] = ("Rain, heavy", "Pioggia forte", "weather-pouring"),
        [66] = ("Freezing rain, light", "Pioggia gelata debole", "weather-rainy"),
        [67] = ("Freezing rain, heavy", "Pioggia gelata forte", "weather-pouring"),
        [71] = ("Snow fall, slight", "Neve debole", "weather-snowy"),
        [73] = ("Snow fall, moderate", "Neve moderata", "weather-snowy"),
        [75] = ("Snow fall, heavy", "Neve forte", "weather-snowy"),
        [77] = ("Snow grains", "Granuli di neve", "weather-snowy"),
        [80] = ("Rain showers, slight", "Rovesci deboli", "weather-rainy"),
        [81] = ("Rain showers, moderate", "Rovesci moderati", "weather-pouring"),
        [82] = ("Rain showers, violent", "Rovesci violenti", "weather-pouring"),
        [85] = ("Snow showers, slight", "Rovesci di neve deboli", "weather-snowy"),
        [86] = ("Snow showers, heavy", "Rovesci di neve forti", "weather-snowy"),
        [95] = ("Thunderstorm, slight or moderate", "Temporale", "weather-lightning"),
        [96] = ("Thunderstorm with slight hail", "Temporale con grandine debole", "weather-hail"),
        [99] = ("Thunderstorm with heavy hail", "Temporale con grandine forte", "weather-hail"),
    };

    /// <summary>Every code Open-Meteo can emit.</summary>
    public static IReadOnlyCollection<int> Codes => Table.Keys;

    public static bool IsKnown(int code) => Table.ContainsKey(code);

    /// <summary>The description in <paramref name="language"/> (<c>"it"</c> or anything else for English). Unknown codes give "Unknown".</summary>
    public static string Describe(int code, string? language)
    {
        var italian = language is not null && language.StartsWith("it", StringComparison.OrdinalIgnoreCase);
        if (!Table.TryGetValue(code, out var entry))
        {
            return italian ? "Sconosciuto" : "Unknown";
        }

        return italian ? entry.It : entry.En;
    }

    /// <summary>A bundled icon id for the code, switching to night variants for clear and partly cloudy skies.</summary>
    public static string Icon(int code, bool isDay)
    {
        var icon = Table.TryGetValue(code, out var entry) ? entry.Icon : "weather-partly-cloudy";
        if (isDay)
        {
            return icon;
        }

        return icon switch
        {
            "weather-sunny" => "weather-night",
            "weather-partly-cloudy" => "weather-night-partly-cloudy",
            _ => icon,
        };
    }
}
