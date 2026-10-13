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

/// <summary>Creates <c>crypto</c> providers: coin prices from CoinGecko (no key) or the Binance public ticker.</summary>
public sealed class CryptoProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "crypto",
        Name = L("Crypto prices", "Prezzi crypto"),
        Description = L(
            "Price and 24 h change of cryptocurrencies. CoinGecko works without a key but shares a small rate limit (poll every 5 minutes or slower; a free demo key makes it stable); Binance's public ticker allows faster updates. Powered by CoinGecko / Binance market data.",
            "Prezzo e variazione nelle 24 ore delle criptovalute. CoinGecko funziona senza chiave ma condivide un limite basso (aggiorna ogni 5 minuti o più lentamente; una chiave demo gratuita lo rende stabile); il ticker pubblico di Binance permette aggiornamenti più rapidi. Dati di mercato forniti da CoinGecko / Binance."),
        Icon = "bitcoin",
        Group = ProviderGroup.Online,
        Settings =
        [
            Setting.Select("source", L("Source", "Fonte"), "coingecko",
            [
                Setting.Option("coingecko", "CoinGecko", "CoinGecko"),
                Setting.Option("binance", "Binance (public ticker)", "Binance (ticker pubblico)"),
            ]),
            Setting.TextList("coins", L("Coins", "Monete"), ["bitcoin", "ethereum"], L("CoinGecko ids (bitcoin, ethereum, solana…). Symbols such as BTC or ETH also work for the common coins.", "Id di CoinGecko (bitcoin, ethereum, solana…). Per le monete più comuni vanno bene anche i simboli come BTC o ETH."), "bitcoin"),
            Setting.Text("currency", L("Currency", "Valuta"), "eur", L("eur, usd, gbp…", "eur, usd, gbp…")),
            Setting.Interval(300, min: 10, max: 86_400, help: L("Seconds between updates. CoinGecko without a key: 300 or more (the minimum is 60). Binance: 10 or more.", "Secondi tra gli aggiornamenti. CoinGecko senza chiave: 300 o più (il minimo è 60). Binance: 10 o più.")),
            Setting.Secret("apiKey", L("CoinGecko demo API key", "Chiave API demo di CoinGecko"), L("Optional. A free key from coingecko.com gives a stable rate limit.", "Facoltativa. Una chiave gratuita di coingecko.com offre un limite stabile.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var s = new ProviderSettings(config.Settings, Descriptor);
        var binance = s.GetString("source") == "binance";
        var interval = Math.Max(binance ? 10 : 60, s.GetDouble("interval", 300));
        return new CryptoProvider(
            config.Id,
            binance,
            s.GetStringList("coins"),
            (s.GetString("currency") ?? "eur").ToLowerInvariant(),
            s.GetString("apiKey"),
            TimeSpan.FromSeconds(interval),
            ProviderServices.Http(services),
            ProviderServices.Time(services),
            ProviderServices.Logger<CryptoProvider>(services));
    }
}

/// <summary>A price and 24 h change for one coin.</summary>
internal sealed record CoinQuote(string Coin, double? Price, double? Change24h);

/// <summary>Coin id and symbol tables and the parsers of both services.</summary>
internal static class CryptoParsing
{
    private static readonly (string Id, string Symbol, string Name)[] Known =
    [
        ("bitcoin", "BTC", "Bitcoin"), ("ethereum", "ETH", "Ethereum"), ("litecoin", "LTC", "Litecoin"), ("ripple", "XRP", "XRP"),
        ("dogecoin", "DOGE", "Dogecoin"), ("solana", "SOL", "Solana"), ("cardano", "ADA", "Cardano"), ("polkadot", "DOT", "Polkadot"),
        ("binancecoin", "BNB", "BNB"), ("tether", "USDT", "Tether"), ("chainlink", "LINK", "Chainlink"), ("avalanche-2", "AVAX", "Avalanche"),
        ("tron", "TRX", "TRON"), ("stellar", "XLM", "Stellar"), ("monero", "XMR", "Monero"), ("bitcoin-cash", "BCH", "Bitcoin Cash"),
        ("shiba-inu", "SHIB", "Shiba Inu"), ("uniswap", "UNI", "Uniswap"), ("cosmos", "ATOM", "Cosmos"),
    ];

    /// <summary>The CoinGecko id for an id or a well-known symbol (<c>"btc"</c> gives <c>"bitcoin"</c>); anything else is kept, lowercase.</summary>
    public static string CoinId(string input)
    {
        var text = input.Trim().ToLowerInvariant();
        foreach (var (id, symbol, _) in Known)
        {
            if (id == text || symbol.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return text;
    }

    /// <summary>The Binance pair for a coin, e.g. (<c>bitcoin</c>, <c>eur</c>) gives <c>BTCEUR</c>. A full pair such as <c>BTCEUR</c> is kept.</summary>
    public static string BinancePair(string coin, string currency)
    {
        var text = coin.Trim().ToUpperInvariant();
        var quote = currency.ToUpperInvariant();
        if (text.Length > quote.Length + 1 && text.EndsWith(quote, StringComparison.Ordinal))
        {
            return text;
        }

        var known = Known.FirstOrDefault(k => k.Id.Equals(text, StringComparison.OrdinalIgnoreCase) || k.Symbol == text);
        return (known.Symbol ?? text) + quote;
    }

    public static string DisplayName(string coinId) =>
        Known.FirstOrDefault(k => k.Id == coinId).Name ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(coinId.Replace('-', ' '));

    /// <summary>Parses <c>simple/price</c>: <c>{"bitcoin":{"eur":61234.12,"eur_24h_change":-1.23}}</c>. Coins missing from the answer get no values.</summary>
    public static List<CoinQuote> ParseCoinGecko(JsonNode? root, IEnumerable<string> ids, string currency)
    {
        var quotes = new List<CoinQuote>();
        foreach (var id in ids)
        {
            var entry = JsonRead.Get(root, id);
            quotes.Add(new CoinQuote(id, JsonRead.Num(entry, currency), JsonRead.Num(entry, currency + "_24h_change")));
        }

        return quotes;
    }

    /// <summary>Parses a Binance <c>ticker/24hr</c> object whose numbers are strings.</summary>
    public static CoinQuote ParseBinance(string coin, JsonNode? ticker) =>
        new(coin, JsonRead.Num(ticker, "lastPrice"), JsonRead.Num(ticker, "priceChangePercent"));

    public static string Symbol(string currency) => currency.ToLowerInvariant() switch
    {
        "eur" => "€",
        "usd" => "$",
        "gbp" => "£",
        "jpy" => "¥",
        "chf" => "CHF",
        var other => other.ToUpperInvariant(),
    };
}

/// <summary>Polls a price source. See docs/integrations/network-sources.md section 10.2.</summary>
internal sealed class CryptoProvider : NetworkPollingProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly bool _binance;
    private readonly IReadOnlyList<string> _coins;
    private readonly string _currency;
    private readonly string? _apiKey;
    private readonly HttpClient _client;

    public CryptoProvider(string id, bool binance, IReadOnlyList<string> coins, string currency, string? apiKey, TimeSpan interval, HttpClient client, TimeProvider time, ILogger logger)
        : base(id, interval, time, logger, staleAfterSeconds: Math.Max(30, interval.TotalSeconds * 3 + 30))
    {
        _binance = binance;
        _coins = coins.Select(CryptoParsing.CoinId).Distinct().ToList();
        _currency = currency;
        _apiKey = apiKey;
        _client = client;
    }

    protected override async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_coins.Count == 0)
        {
            throw new SourceUnavailableException("Add at least one coin in the settings.");
        }

        var quotes = _binance
            ? await FetchBinanceAsync(cancellationToken).ConfigureAwait(false)
            : await FetchCoinGeckoAsync(cancellationToken).ConfigureAwait(false);

        var symbol = CryptoParsing.Symbol(_currency);
        foreach (var quote in quotes)
        {
            var slug = LocalIds.Slug(quote.Coin, "coin");
            var name = CryptoParsing.DisplayName(quote.Coin);
            var icon = quote.Coin switch { "bitcoin" => "bitcoin", "ethereum" => "ethereum", _ => _currency == "usd" ? "currency-usd" : _currency == "eur" ? "currency-eur" : "chart-line" };
            Num(Info($"{slug}/price", $"{name} price", SensorCategory.Finance, symbol, name, null, SensorKinds.Price, 0, null, icon), quote.Price);
            Num(Info($"{slug}/change", $"{name} 24 h change", SensorCategory.Finance, Units.Percent, name, null, SensorKinds.Percent, -20, 20, quote.Change24h is < 0 ? "trending-down" : "trending-up"), quote.Change24h);
            if (quote.Price is null)
            {
                Warning ??= $"No price for '{quote.Coin}'. {(_binance ? "Check the pair on Binance." : "Use the CoinGecko id, for example 'bitcoin'.")}";
            }
        }
    }

    private async Task<List<CoinQuote>> FetchCoinGeckoAsync(CancellationToken cancellationToken)
    {
        var url = "https://api.coingecko.com/api/v3/simple/price?ids=" + Uri.EscapeDataString(string.Join(',', _coins)) +
                  "&vs_currencies=" + Uri.EscapeDataString(_currency) + "&include_24hr_change=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-cg-demo-api-key", _apiKey);
        }

        var response = NetHttp.EnsureSuccess(await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false), "API key");
        return CryptoParsing.ParseCoinGecko(response.Json(), _coins, _currency);
    }

    private async Task<List<CoinQuote>> FetchBinanceAsync(CancellationToken cancellationToken)
    {
        var quotes = new List<CoinQuote>();
        foreach (var coin in _coins)
        {
            var pair = CryptoParsing.BinancePair(coin, _currency);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://data-api.binance.vision/api/v3/ticker/24hr?symbol=" + pair);
            var response = await NetHttp.SendAsync(_client, request, RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Status == (HttpStatusCode)418)
            {
                throw new RateLimitedException("Binance has temporarily banned this IP address for too many requests.", NetHttp.RetryAfter(response.Headers) ?? TimeSpan.FromMinutes(5));
            }

            if (response.Status == HttpStatusCode.BadRequest)
            {
                quotes.Add(new CoinQuote(coin, null, null)); // unknown pair: reported as a warning, the others still work
                continue;
            }

            quotes.Add(CryptoParsing.ParseBinance(coin, NetHttp.EnsureSuccess(response).Json()));
        }

        return quotes;
    }
}
