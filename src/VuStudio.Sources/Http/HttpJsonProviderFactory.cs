using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Http;

/// <summary>Creates <c>http-json</c> providers: values from any JSON web API.</summary>
public sealed class HttpJsonProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "http-json",
        Name = L("HTTP JSON", "HTTP JSON"),
        Description = L(
            "Calls a web API on an interval and turns values from its JSON answer into sensors.",
            "Chiama un'API web a intervalli regolari e trasforma i valori della risposta JSON in sensori."),
        Icon = "api",
        Group = ProviderGroup.Online,
        Singleton = false,
        AutoCreate = false,
        Platforms = PlatformSupport.Any,
        Settings =
        [
            Setting.Url(
                "url",
                L("URL", "URL"),
                help: L("The http or https address to call.", "L'indirizzo http o https da chiamare."),
                placeholder: "https://api.example.com/v1/status",
                required: true),
            Setting.Select(
                "method",
                L("Method", "Metodo"),
                "GET",
                [Setting.Option("GET", "GET", "GET"), Setting.Option("POST", "POST", "POST")]),
            Setting.Table(
                "headers",
                L("Headers", "Intestazioni"),
                [
                    Setting.Text("name", L("Name", "Nome"), placeholder: "Authorization"),
                    Setting.Secret(
                        "value",
                        L("Value", "Valore"),
                        L("Hidden in the interface because it often holds a token.", "Nascosto nell'interfaccia perché spesso contiene un token.")),
                ],
                help: L("Extra request headers, for example an API key.", "Intestazioni aggiuntive della richiesta, ad esempio una chiave API.")),
            Setting.Multiline(
                "body",
                L("Request body", "Corpo della richiesta"),
                help: L(
                    "Sent with POST requests. It is JSON unless you add a Content-Type header.",
                    "Inviato con le richieste POST. È JSON a meno che tu non aggiunga un'intestazione Content-Type.")),
            Setting.Interval(
                HttpJsonOptions.DefaultIntervalSeconds,
                min: 1,
                help: L(
                    "Seconds between requests. Be kind to public APIs: most do not need more than once a minute.",
                    "Secondi tra una richiesta e l'altra. Rispetta le API pubbliche: la maggior parte non richiede più di una chiamata al minuto.")),
            Setting.Seconds(
                "timeout",
                L("Timeout", "Timeout"),
                HttpJsonOptions.DefaultTimeoutSeconds,
                min: 0.5,
                max: HttpJsonOptions.MaxTimeoutSeconds,
                help: L("Give up on a request after this many seconds.", "Rinuncia alla richiesta dopo questi secondi.")),
            FieldSpecs.Table(
                L("Fields", "Campi"),
                L("JSON path", "Percorso JSON"),
                L(
                    "Where the value is in the answer: $.main.temp, data[0].price, $['key with space'], items[-1].",
                    "Dove si trova il valore nella risposta: $.main.temp, data[0].price, $['chiave con spazi'], items[-1]."),
                L(
                    "Each row becomes a sensor. Leave the table empty to see every value of the answer as a sensor and pick from them.",
                    "Ogni riga diventa un sensore. Lascia la tabella vuota per vedere ogni valore della risposta come sensore e sceglierne uno.")),
        ],
    };

    /// <summary>
    /// A source last changed from the LAN gets a private client that refuses loopback and link-local destinations
    /// (see <see cref="DestinationGuard"/>); one set up on this PC may call local services.
    /// </summary>
    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var restricted = config.ChangedRemotely ? DestinationGuard.CreateRestrictedClient() : null;
        return new HttpJsonProvider(
            config.Id,
            HttpJsonOptions.From(new ProviderSettings(config.Settings, Descriptor)),
            restricted ?? ProviderServices.Http(services),
            string.IsNullOrWhiteSpace(config.Name) ? null : config.Name.Trim(),
            ProviderServices.Logger<HttpJsonProvider>(services),
            restricted);
    }
}
