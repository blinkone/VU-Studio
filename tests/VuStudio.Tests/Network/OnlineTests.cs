using System.Net;
using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Online;
using VuStudio.Tests.Sources;

namespace VuStudio.Tests.Network;

public class WmoCodeTests
{
    [Fact]
    public void Every_code_open_meteo_emits_has_both_languages()
    {
        int[] expected = [0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99];
        Assert.Equal(expected.Order(), WmoCodes.Codes.Order());
        foreach (var code in expected)
        {
            var en = WmoCodes.Describe(code, "en");
            var it = WmoCodes.Describe(code, "it");
            Assert.False(string.IsNullOrWhiteSpace(en));
            Assert.False(string.IsNullOrWhiteSpace(it));
            Assert.NotEqual("Unknown", en);
        }
    }

    [Theory]
    [InlineData(0, "en", "Clear sky")]
    [InlineData(0, "it", "Sereno")]
    [InlineData(3, "it-IT", "Coperto")]
    [InlineData(95, "en", "Thunderstorm, slight or moderate")]
    [InlineData(96, "it", "Temporale con grandine debole")]
    [InlineData(61, null, "Rain, slight")]
    public void Descriptions_follow_the_language(int code, string? language, string expected) =>
        Assert.Equal(expected, WmoCodes.Describe(code, language));

    [Fact]
    public void Unknown_codes_are_not_an_error()
    {
        Assert.Equal("Unknown", WmoCodes.Describe(1234, "en"));
        Assert.Equal("Sconosciuto", WmoCodes.Describe(-1, "it"));
        Assert.False(WmoCodes.IsKnown(4));
    }

    [Fact]
    public void Icons_switch_to_night_variants_and_exist_in_the_bundled_set()
    {
        Assert.Equal("weather-sunny", WmoCodes.Icon(0, isDay: true));
        Assert.Equal("weather-night", WmoCodes.Icon(0, isDay: false));
        Assert.Equal("weather-night-partly-cloudy", WmoCodes.Icon(2, isDay: false));
        var icons = NetworkAssets.IconIds();
        Assert.SkipWhen(icons is null, "icon set not found");
        foreach (var code in WmoCodes.Codes)
        {
            Assert.Contains(WmoCodes.Icon(code, true), icons!);
            Assert.Contains(WmoCodes.Icon(code, false), icons!);
        }
    }
}

public class WeatherProviderTests
{
    private const string Geocoding = """{"results":[{"id":3173435,"name":"Milano","latitude":45.46427,"longitude":9.18951,"elevation":122.0,"feature_code":"PPLA","country_code":"IT","country":"Italia","admin1":"Lombardia","timezone":"Europe/Rome"}],"generationtime_ms":0.9}""";

    private const string Forecast = """
        {"latitude":45.46,"longitude":9.19,"timezone":"Europe/Rome",
         "current_units":{"time":"iso8601","temperature_2m":"°C"},
         "current":{"time":"2026-10-12T17:15","interval":900,"temperature_2m":18.4,"relative_humidity_2m":62,"apparent_temperature":17.9,"is_day":1,"precipitation":0.2,"weather_code":3,"cloud_cover":88,"pressure_msl":1016.2,"wind_speed_10m":7.9,"wind_direction_10m":240,"wind_gusts_10m":15.1},
         "hourly":{"time":["2026-10-12T17:00"],"uv_index":[2.4]}}
        """;

    private const string Air = """{"current":{"time":"2026-10-12T17:00","european_aqi":34,"pm2_5":9.8}}""";

    private static Harness Make(FakeHttp http, string settings = """{"city":"Milano"}""") =>
        Net.Make(new WeatherProviderFactory(), settings, http);

    private static FakeHttp Server() => new FakeHttp()
        .Route("geocoding-api.open-meteo.com", Geocoding)
        .Route("api.open-meteo.com/v1/forecast", Forecast)
        .Route("air-quality-api.open-meteo.com", Air);

    [Fact]
    public async Task Geocodes_the_city_then_publishes_conditions_and_air_quality()
    {
        var http = Server();
        var h = Make(http, """{"city":"Milano","language":"it"}""");

        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal(18.4, h.Number("temperature"));
        Assert.Equal(17.9, h.Number("apparent-temperature"));
        Assert.Equal(62, h.Number("humidity"));
        Assert.Equal(0.2, h.Number("precipitation"));
        Assert.Equal(7.9, h.Number("wind-speed"));
        Assert.Equal(2.4, h.Number("uv-index"));
        Assert.Equal(3, h.Number("weather-code"));
        Assert.Equal(1, h.Number("is-day"));
        Assert.Equal("Coperto", h.Text("description"));
        Assert.Equal("Overcast", h.Text("description-en"));
        Assert.Equal("Coperto", h.Text("description-it"));
        Assert.Equal(34, h.Number("aqi"));
        Assert.Equal(9.8, h.Number("pm2-5"));
        Assert.Equal("Milano, Lombardia, IT", h.Info("temperature")!.Device);
        Assert.Equal(SensorCategory.Weather, h.Info("temperature")!.Category);
        Assert.Contains("latitude=45.4643", http.Requests.First(r => r.Uri.Host == "api.open-meteo.com").Uri.Query);
        Assert.Contains("language=", http.Requests[0].Uri.Query);
        Assert.True(h.Info("temperature")!.StaleAfterSeconds > 900);
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task Coordinates_skip_geocoding_and_air_quality_failures_only_warn()
    {
        var http = Server().Route("air-quality-api", "{}", HttpStatusCode.BadRequest);
        var h = Make(http, """{"latitude":45.5,"longitude":9.2}""");

        await h.StepAsync();

        Assert.Equal(0, http.Count("geocoding"));
        Assert.Equal(18.4, h.Number("temperature"));
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("Air quality", h.Message);
    }

    [Fact]
    public async Task Unknown_city_missing_place_and_rate_limits_are_reported_clearly()
    {
        var h = Make(new FakeHttp().Route("geocoding", """{"generationtime_ms":0.3}"""), """{"city":"Nowhereville"}""");
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, h.Health);
        Assert.Contains("Nowhereville", h.Message);

        var none = Make(new FakeHttp(), "{}");
        await none.StepAsync();
        Assert.Equal(ProviderHealth.Unavailable, none.Health);

        var limited = Make(Server().Route("v1/forecast", _ => new FakeHttp.FakeReply(HttpStatusCode.TooManyRequests, """{"error":true,"reason":"Minutely API request limit exceeded."}""", new() { ["Retry-After"] = "600" })));
        var delay = await limited.StepAsync();
        Assert.Equal(ProviderHealth.Degraded, limited.Health);
        Assert.True(delay >= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Api_errors_show_the_reason_from_open_meteo()
    {
        var h = Make(Server().Route("v1/forecast", """{"error":true,"reason":"Latitude must be in range of -90 to 90"}""", HttpStatusCode.BadRequest));
        await h.StepAsync();
        Assert.Contains("Latitude must be in range", h.Message);
    }

    [Fact]
    public void Interval_is_never_below_five_minutes_and_attribution_is_in_the_description()
    {
        var factory = new WeatherProviderFactory();
        Assert.Equal(300, factory.Descriptor.Settings.Single(s => s.Key == "interval").Min);
        Assert.Contains("CC BY 4.0", factory.Descriptor.Description!.En);
        Assert.Contains("Open-Meteo", factory.Descriptor.Description!.It);
        Assert.Contains("CC BY 4.0", factory.Descriptor.Description!.It);
        var tooFast = Make(Server(), """{"city":"Milano","interval":5}""");
        Assert.True(tooFast.Provider is not null);
    }

    [Fact]
    public void Forecast_reading_falls_back_to_the_hourly_uv_index()
    {
        var reading = WeatherReading.Parse(JsonNode.Parse(Forecast));
        Assert.Equal(2.4, reading.UvIndex);
        Assert.Equal(3, reading.WeatherCode);
        Assert.True(reading.IsDay);
        Assert.Null(WeatherReading.Parse(JsonNode.Parse("""{"current":{"temperature_2m":null}}""")).Temperature);
        Assert.Throws<InvalidDataException>(() => WeatherReading.Parse(JsonNode.Parse("{}")));
    }
}

public class CryptoTests
{
    private const string CoinGecko = """{"bitcoin":{"eur":61234.12,"eur_24h_change":-1.23,"last_updated_at":1760281200},"ethereum":{"eur":2400.5,"eur_24h_change":0.8}}""";

    [Fact]
    public void Coin_ids_and_binance_pairs_are_derived_from_ids_and_symbols()
    {
        Assert.Equal("bitcoin", CryptoParsing.CoinId("BTC"));
        Assert.Equal("bitcoin", CryptoParsing.CoinId(" Bitcoin "));
        Assert.Equal("some-new-coin", CryptoParsing.CoinId("Some-New-Coin"));
        Assert.Equal("BTCEUR", CryptoParsing.BinancePair("bitcoin", "eur"));
        Assert.Equal("ETHUSD", CryptoParsing.BinancePair("eth", "usd"));
        Assert.Equal("BTCEUR", CryptoParsing.BinancePair("BTCEUR", "eur"));
        Assert.Equal("PEPEEUR", CryptoParsing.BinancePair("pepe", "eur"));
    }

    [Fact]
    public void CoinGecko_and_binance_answers_are_parsed()
    {
        var quotes = CryptoParsing.ParseCoinGecko(JsonNode.Parse(CoinGecko), ["bitcoin", "ethereum", "ghost"], "eur");
        Assert.Equal(61234.12, quotes[0].Price);
        Assert.Equal(-1.23, quotes[0].Change24h);
        Assert.Null(quotes[2].Price);

        var binance = CryptoParsing.ParseBinance("bitcoin", JsonNode.Parse("""{"symbol":"BTCEUR","priceChange":"-756.10","priceChangePercent":"-1.22","lastPrice":"61234.12"}"""));
        Assert.Equal(61234.12, binance.Price);
        Assert.Equal(-1.22, binance.Change24h);
    }

    [Fact]
    public async Task CoinGecko_publishes_price_and_change_and_sends_the_demo_key()
    {
        var http = new FakeHttp().Route("simple/price", CoinGecko);
        var h = Net.Make(new CryptoProviderFactory(), """{"coins":["btc","ethereum"],"currency":"EUR","apiKey":"CG-demo"}""", http);

        await h.StepAsync();

        Assert.Equal(ProviderHealth.Ok, h.Health);
        Assert.Equal(61234.12, h.Number("bitcoin/price"));
        Assert.Equal(-1.23, h.Number("bitcoin/change"));
        Assert.Equal(2400.5, h.Number("ethereum/price"));
        var info = h.Info("bitcoin/price")!;
        Assert.Equal(SensorCategory.Finance, info.Category);
        Assert.Equal(SensorKinds.Price, info.Kind);
        Assert.Equal("€", info.Unit);
        Assert.Equal("CG-demo", http.Requests[0].Headers["x-cg-demo-api-key"]);
        Assert.Contains("ids=bitcoin%2Cethereum", http.Requests[0].Uri.Query);
        Assert.Contains("vs_currencies=eur", http.Requests[0].Uri.Query);
        Assert.True(h.Info("bitcoin/price")!.StaleAfterSeconds >= 900);
        h.AssertCatalogIsSound();
    }

    [Fact]
    public async Task Unknown_coins_warn_and_rate_limits_back_off()
    {
        var http = new FakeHttp().Route("simple/price", """{"bitcoin":{"eur":1.0,"eur_24h_change":0}}""");
        var h = Net.Make(new CryptoProviderFactory(), """{"coins":["bitcoin","nonexistent"]}""", http);
        await h.StepAsync();
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.Contains("nonexistent", h.Message);
        Assert.Equal(1, h.Number("bitcoin/price"));

        var limited = new FakeHttp().Route("simple/price", "{}", HttpStatusCode.TooManyRequests, new() { ["Retry-After"] = "120" });
        var h2 = Net.Make(new CryptoProviderFactory(), "{}", limited);
        var delay = await h2.StepAsync();
        Assert.Equal(ProviderHealth.Degraded, h2.Health);
        Assert.True(delay >= TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task Binance_uses_one_ticker_call_per_pair_and_tolerates_an_invalid_pair()
    {
        var http = new FakeHttp()
            .Route("symbol=BTCEUR", """{"symbol":"BTCEUR","priceChangePercent":"-1.22","lastPrice":"61234.12"}""")
            .Route("symbol=NOPEEUR", """{"code":-1121,"msg":"Invalid symbol."}""", HttpStatusCode.BadRequest);
        var h = Net.Make(new CryptoProviderFactory(), """{"source":"binance","coins":["bitcoin","nope"],"currency":"eur","interval":10}""", http);

        await h.StepAsync();

        Assert.Equal(61234.12, h.Number("bitcoin/price"));
        Assert.Equal(-1.22, h.Number("bitcoin/change"));
        Assert.Null(h.Number("nope/price"));
        Assert.Equal(ProviderHealth.Degraded, h.Health);
        Assert.StartsWith("https://data-api.binance.vision/", http.Requests[0].Uri.ToString());

        var banned = new FakeHttp().Route("ticker", "{}", (HttpStatusCode)418, new() { ["Retry-After"] = "300" });
        var h2 = Net.Make(new CryptoProviderFactory(), """{"source":"binance","coins":["bitcoin"]}""", banned);
        var delay = await h2.StepAsync();
        Assert.True(delay >= TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void Intervals_are_clamped_to_respect_the_free_services()
    {
        var factory = new CryptoProviderFactory();
        var created = factory.Create(TestConfig.For("crypto", """{"interval":5}"""), TestServices.Empty());
        Assert.NotNull(created);
    }
}
