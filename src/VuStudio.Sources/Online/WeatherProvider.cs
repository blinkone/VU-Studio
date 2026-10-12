using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.Remote;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Online;

/// <summary>Creates <c>weather</c> providers: current conditions and air quality from Open-Meteo (no key needed).</summary>
public sealed class WeatherProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "weather",
        Name = L("Weather (Open-Meteo)", "Meteo (Open-Meteo)"),
        Description = L(
            "Current weather and air quality for a city or coordinates. No account needed. Weather data by Open-Meteo.com (https://open-meteo.com/), licensed under CC BY 4.0; air quality data from Copernicus CAMS via Open-Meteo. Free for non-commercial use.",
            "Meteo attuale e qualità dell'aria per una città o delle coordinate. Non serve alcun account. Dati meteo di Open-Meteo.com (https://open-meteo.com/), con licenza CC BY 4.0; dati sulla qualità dell'aria di Copernicus CAMS tramite Open-Meteo. Gratuito per uso non commerciale."),
        Icon = "weather-partly-cloudy",
        Group = ProviderGroup.Online,
        Settings =
        [
            Setting.Text("city", L("City", "Città"), help: L("Looked up with Open-Meteo geocoding, for example 'Milano' or 'Rome, Lazio'. Ignored when coordinates are given.", "Cercata con il geocoding di Open-Meteo, ad esempio 'Milano' o 'Roma, Lazio'. Ignorata se inserisci le coordinate."), placeholder: "Milano"),
            Setting.Number("latitude", L("Latitude", "Latitudine"), null, -90, 90, L("Optional, instead of the city.", "Facoltativa, al posto della città.")),
            Setting.Number("longitude", L("Longitude", "Longitudine"), null, -180, 180, L("Optional, instead of the city.", "Facoltativa, al posto della città.")),
            Setting.Seconds("interval", L("Update interval", "Intervallo di aggiornamento"), 900, 300, 86_400, L("The forecast model changes every 15 minutes; never below 5 minutes to respect the free service.", "Il modello meteo cambia ogni 15 minuti; mai sotto i 5 minuti per rispettare il servizio gratuito.")),
            Setting.Select("language", L("Description language", "Lingua della descrizione"), "auto",
            [
                Setting.Option("auto", "Same as the interface", "Come l'interfaccia"),
                Setting.Option("en", "English", "Inglese"),
                Setting.Option("it", "Italian", "Italiano"),
            ]),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var language = s.GetString("language", "auto");
        if (language == "auto")
        {
            language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";
        }

        return new WeatherProvider(
            config.Id,
            s.GetString("city"),
            Coordinate(s, "latitude", 90),
            Coordinate(s, "longitude", 180),
            language!,
            TimeSpan.FromSeconds(Math.Max(300, s.GetDouble("interval", 900))),
            ProviderServices.Http(services),
            ProviderServices.Time(services),
            ProviderServices.Logger<WeatherProvider>(services));
    }

    private static double? Coordinate(ProviderSettings s, string key, double limit)
    {
        var value = s.GetDouble(key, double.NaN);
        return double.IsFinite(value) && Math.Abs(value) <= limit ? value : null;
    }
}

/// <summary>One reading of the Open-Meteo forecast API (<c>current</c> block).</summary>
internal sealed record WeatherReading(
    double? Temperature, double? Apparent, double? Humidity, double? Precipitation, double? WindSpeed, double? WindDirection, double? WindGusts,
    double? Pressure, double? CloudCover, double? UvIndex, int? WeatherCode, bool? IsDay)
{
    public static WeatherReading Parse(JsonNode? root)
    {
        var current = JsonRead.Get(root, "current") ?? throw new InvalidDataException("Open-Meteo returned no current conditions.");
        var uv = JsonRead.Num(current, "uv_index");
        if (uv is null && JsonRead.Path(root, "hourly", "uv_index") is JsonArray { Count: > 0 } hourly)
        {
            uv = JsonRead.Num(hourly[0]);
        }

        return new WeatherReading(
            JsonRead.Num(current, "temperature_2m"),
            JsonRead.Num(current, "apparent_temperature"),
            JsonRead.Num(current, "relative_humidity_2m"),
            JsonRead.Num(current, "precipitation"),
            JsonRead.Num(current, "wind_speed_10m"),
            JsonRead.Num(current, "wind_direction_10m"),
            JsonRead.Num(current, "wind_gusts_10m"),
            JsonRead.Num(current, "pressure_msl"),
            JsonRead.Num(current, "cloud_cover"),
            uv,
            JsonRead.Num(current, "weather_code") is { } code ? (int)code : null,
            JsonRead.Num(current, "is_day") is { } day ? day != 0 : null);
    }
}

internal sealed record GeoPlace(string Name, double Latitude, double Longitude, string? Region, string? Country)
{
    public string Label => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

    public static GeoPlace? ParseFirst(JsonNode? root)
    {
        var first = JsonRead.Items(root, "results").FirstOrDefault();
        if (first is null || JsonRead.Num(first, "latitude") is not { } lat || JsonRead.Num(first, "longitude") is not { } lon)
        {
            return null;
        }

        return new GeoPlace(JsonRead.Str(first, "name") ?? "?", lat, lon, JsonRead.Str(first, "admin1"), JsonRead.Str(first, "country_code"));
    }
}

/// <summary>Reads Open-Meteo. See docs/integrations/network-sources.md section 10.1.</summary>
internal sealed class WeatherProvider : NetworkPollingProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string? _city;
    private readonly string _language;
    private readonly HttpClient _client;
    private double? _latitude;
    private double? _longitude;
    private string? _place;

    public WeatherProvider(string id, string? city, double? latitude, double? longitude, string language, TimeSpan interval, HttpClient client, TimeProvider time, ILogger logger)
        : base(id, interval < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : interval, time, logger)
    {
        _city = city;
        _latitude = latitude;
        _longitude = longitude;
        _language = language;
        _client = client;
        if (latitude is not null && longitude is not null)
        {
            _place = city ?? string.Create(CultureInfo.InvariantCulture, $"{latitude:0.###}, {longitude:0.###}");
        }
    }

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_latitude is null || _longitude is null)
        {
            await GeocodeAsync(cancellationToken).ConfigureAwait(false);
        }

        var lat = _latitude!.Value.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = _longitude!.Value.ToString("0.####", CultureInfo.InvariantCulture);
        var forecast = await GetAsync(
            "https://api.open-meteo.com/v1/forecast?latitude=" + lat + "&longitude=" + lon +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m" +
            "&hourly=uv_index&forecast_hours=1&timezone=auto&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm",
            cancellationToken).ConfigureAwait(false);
        PublishWeather(WeatherReading.Parse(forecast));

        try
        {
            var air = await GetAsync($"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={lat}&longitude={lon}&current=european_aqi,pm2_5&timezone=auto", cancellationToken).ConfigureAwait(false);
            PublishAir(JsonRead.Get(air, "current"));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or RateLimitedException)
        {
            Warning = $"Air quality unavailable: {ex.Message}";
        }
    }

    private async Task GeocodeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_city))
        {
            throw new SourceUnavailableException("Enter a city or the latitude and longitude in the settings.");
        }

        var root = await GetAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(_city)}&count=1&language={_language}&format=json", cancellationToken).ConfigureAwait(false);
        var place = GeoPlace.ParseFirst(root) ?? throw new SourceUnavailableException($"Open-Meteo does not know a place called '{_city}'.");
        _latitude = place.Latitude;
        _longitude = place.Longitude;
        _place = place.Label;
    }

    private async Task<JsonNode?> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess && response.Status != HttpStatusCode.TooManyRequests)
        {
            string? reason = null;
            try
            {
                reason = JsonRead.Str(response.Json(), "reason");
            }
            catch (InvalidDataException)
            {
            }

            throw new IOException($"Open-Meteo answered HTTP {(int)response.Status}{(reason is null ? "" : ": " + reason)}.");
        }

        return NetHttp.EnsureSuccess(response).Json();
    }

    private SensorInfo S(string id, string name, SensorCategory category, string unit, string? kind = null, double? min = null, double? max = null, string? icon = null, bool text = false)
    {
        var info = Info(id, name, category, unit, _place, null, kind, min, max, icon, text);
        return info with { StaleAfterSeconds = Interval.TotalSeconds * 3 + 60 };
    }

    private void PublishWeather(WeatherReading w)
    {
        Num(S("temperature", "Temperature", SensorCategory.Weather, Units.Celsius, SensorKinds.Temperature, -20, 45, "thermometer"), w.Temperature);
        Num(S("apparent-temperature", "Feels like", SensorCategory.Weather, Units.Celsius, SensorKinds.Temperature, -20, 45, "sun-thermometer"), w.Apparent);
        Num(S("humidity", "Humidity", SensorCategory.Weather, Units.Percent, SensorKinds.Humidity, 0, 100, "water-percent"), w.Humidity);
        Num(S("precipitation", "Precipitation", SensorCategory.Weather, "mm", SensorKinds.Level, 0, 10, "weather-rainy"), w.Precipitation);
        Num(S("wind-speed", "Wind speed", SensorCategory.Weather, "km/h", SensorKinds.Speed, 0, 100, "weather-windy"), w.WindSpeed);
        Num(S("wind-gusts", "Wind gusts", SensorCategory.Weather, "km/h", SensorKinds.Speed, 0, 120, "weather-windy"), w.WindGusts);
        Num(S("wind-direction", "Wind direction", SensorCategory.Weather, "°", SensorKinds.Level, 0, 360, "weather-windy"), w.WindDirection);
        Num(S("pressure", "Pressure", SensorCategory.Weather, "hPa", SensorKinds.Level, 960, 1060, "gauge"), w.Pressure);
        Num(S("cloud-cover", "Cloud cover", SensorCategory.Weather, Units.Percent, SensorKinds.Percent, 0, 100, "weather-cloudy"), w.CloudCover);
        Num(S("uv-index", "UV index", SensorCategory.Weather, Units.None, SensorKinds.Level, 0, 11, "white-balance-sunny"), w.UvIndex);
        Num(S("is-day", "Is daytime", SensorCategory.Weather, Units.None, SensorKinds.State, 0, 1, "white-balance-sunny"), w.IsDay is { } day ? (day ? 1 : 0) : null);

        var icon = w.WeatherCode is { } c ? WmoCodes.Icon(c, w.IsDay ?? true) : "weather-partly-cloudy";
        Num(S("weather-code", "Weather code (WMO)", SensorCategory.Weather, Units.None, SensorKinds.State, 0, 99, icon), w.WeatherCode);
        Txt(S("description", "Conditions", SensorCategory.Weather, Units.None, SensorKinds.Text, icon: icon, text: true), w.WeatherCode is { } code ? WmoCodes.Describe(code, _language) : null);
        Txt(S("description-en", "Conditions (English)", SensorCategory.Weather, Units.None, SensorKinds.Text, icon: icon, text: true), w.WeatherCode is { } en ? WmoCodes.Describe(en, "en") : null);
        Txt(S("description-it", "Conditions (Italian)", SensorCategory.Weather, Units.None, SensorKinds.Text, icon: icon, text: true), w.WeatherCode is { } it ? WmoCodes.Describe(it, "it") : null);
    }

    private void PublishAir(JsonNode? current)
    {
        Num(S("aqi", "Air quality index (European)", SensorCategory.Weather, Units.None, SensorKinds.Level, 0, 100, "air-filter"), JsonRead.Num(current, "european_aqi"));
        Num(S("pm2-5", "PM2.5", SensorCategory.Weather, "µg/m³", SensorKinds.Level, 0, 50, "molecule-co2"), JsonRead.Num(current, "pm2_5"));
    }
}
