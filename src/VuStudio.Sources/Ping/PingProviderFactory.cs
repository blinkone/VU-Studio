using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Pings;

/// <summary>Creates <c>ping</c> providers: latency, packet loss and reachability of hosts and of the default gateway.</summary>
public sealed class PingProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "ping",
        Name = L("Ping", "Ping"),
        Description = L(
            "Latency, packet loss and reachability of hosts and of your default gateway, to tell whether the problem is the internet or the router.",
            "Latenza, perdita di pacchetti e raggiungibilità di host e del gateway predefinito, per capire se il problema è internet o il router."),
        Icon = "lan",
        Group = ProviderGroup.Network,
        Singleton = false,
        AutoCreate = false,
        Platforms = PlatformSupport.Any,
        Settings =
        [
            Setting.Table(
                "targets",
                L("Targets", "Destinazioni"),
                [
                    Setting.Text(
                        "host",
                        L("Host", "Host"),
                        help: L("A host name or an IP address.", "Un nome host o un indirizzo IP."),
                        placeholder: "1.1.1.1",
                        required: true),
                    Setting.Text("name", L("Name", "Nome"), placeholder: "Cloudflare"),
                ],
                new JsonArray(
                    Setting.Row(("host", "1.1.1.1"), ("name", "Cloudflare")),
                    Setting.Row(("host", "8.8.8.8"), ("name", "Google DNS"))),
                L(
                    "Hosts to ping. The sensor \"internet/up\" is on while any of them answers.",
                    "Host da pingare. Il sensore \"internet/up\" è attivo finché almeno uno risponde.")),
            Setting.Bool(
                "includeGateway",
                L("Ping the default gateway", "Pinga il gateway predefinito"),
                true,
                L(
                    "Also ping your router (the default gateway), found automatically. It is reported separately and does not count for \"internet/up\".",
                    "Pinga anche il router (il gateway predefinito), trovato automaticamente. È riportato a parte e non conta per \"internet/up\".")),
            Setting.Interval(
                PingSettings.DefaultIntervalSeconds,
                min: 1,
                help: L("Seconds between rounds of pings.", "Secondi tra un giro di ping e il successivo.")),
            Setting.Number(
                "timeoutMs",
                L("Timeout (ms)", "Timeout (ms)"),
                PingSettings.DefaultTimeoutMs,
                min: 100,
                max: 30_000,
                help: L("A ping that is not answered within this time counts as lost.", "Un ping senza risposta entro questo tempo conta come perso.")),
            Setting.Number(
                "window",
                L("Loss window (pings)", "Finestra della perdita (ping)"),
                PingSettings.DefaultWindow,
                min: 1,
                max: 1000,
                help: L(
                    "Packet loss is the share of lost pings among the last this-many pings.",
                    "La perdita di pacchetti è la quota di ping persi tra gli ultimi di questo numero.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services) =>
        new PingProvider(
            config.Id,
            PingSettings.From(new ProviderSettings(config.Settings, Descriptor)),
            new SystemPinger(),
            new NetworkInterfaceGatewayLocator(),
            ProviderServices.Logger<PingProvider>(services));
}
