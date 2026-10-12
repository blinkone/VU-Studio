using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Demo;

/// <summary>Creates the <c>demo</c> provider: synthetic signals for testing faces and lights.</summary>
public sealed class DemoProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "demo",
        Name = L("Demo signals", "Segnali dimostrativi"),
        Description = L(
            "Synthetic waves, a drifting temperature, bursty network traffic and an audio-like level, for trying out faces, needles and lights.",
            "Onde sintetiche, una temperatura che deriva, traffico di rete a raffiche e un livello simile all'audio, per provare quadranti, lancette e luci."),
        Icon = "sine-wave",
        Group = ProviderGroup.Utility,
        Singleton = true,
        AutoCreate = false,
        Platforms = PlatformSupport.Any,
        Settings =
        [
            Setting.Seconds(
                "period",
                L("Wave period", "Periodo delle onde"),
                10,
                min: 0.2,
                max: 3600,
                help: L(
                    "Length of one cycle of the sine, sawtooth and square waves, in seconds.",
                    "Durata di un ciclo delle onde sinusoidale, a dente di sega e quadra, in secondi.")),
            Setting.Number(
                "seed",
                L("Random seed", "Seme casuale"),
                42,
                help: L(
                    "The same seed always produces the same temperature, network and audio signals.",
                    "Lo stesso seme produce sempre gli stessi segnali di temperatura, rete e audio.")),
            Setting.Interval(0.1, min: 0.05, max: 10),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services) =>
        new DemoProvider(
            config.Id,
            new ProviderSettings(config.Settings, Descriptor),
            ProviderServices.Time(services),
            ProviderServices.Logger<DemoProvider>(services));
}
