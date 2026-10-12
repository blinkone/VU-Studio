using Microsoft.Extensions.DependencyInjection;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Push;

/// <summary>
/// Creates the <c>push</c> provider. It uses the <see cref="PushGateway"/> registered in the host, or
/// <see cref="PushGateway.Shared"/> when there is none.
/// </summary>
public sealed class PushProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "push",
        Name = L("Push API", "API push"),
        Description = L(
            "Lets other programs (scripts, Stream Deck, Home Assistant…) create sensors and push values through the local API.",
            "Permette ad altri programmi (script, Stream Deck, Home Assistant…) di creare sensori e inviare valori tramite l'API locale."),
        Icon = "upload-network",
        Group = ProviderGroup.Utility,
        Singleton = true,
        AutoCreate = true,
        Platforms = PlatformSupport.Any,
        Settings =
        [
            Setting.Seconds(
                "staleAfter",
                L("Value validity", "Validità dei valori"),
                120,
                min: 1,
                max: 86_400,
                help: L(
                    "A pushed value is treated as offline when it has not been refreshed for this many seconds. A push can override this per sensor.",
                    "Un valore inviato è considerato offline se non viene aggiornato da tanti secondi. Ogni invio può cambiarlo per il singolo sensore.")),
            Setting.Number(
                "maxSensors",
                L("Maximum sensors", "Numero massimo di sensori"),
                500,
                min: 1,
                max: 10_000,
                help: L(
                    "Upper limit to the number of sensors external programs can create.",
                    "Limite massimo al numero di sensori che i programmi esterni possono creare.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services) =>
        new PushProvider(
            config.Id,
            services.GetService<PushGateway>() ?? PushGateway.Shared,
            new ProviderSettings(config.Settings, Descriptor));
}
