using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Processes;

/// <summary>Creates <c>process</c> providers: running state, CPU and memory of named programs.</summary>
public sealed class ProcessProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "process",
        Name = L("Processes", "Processi"),
        Description = L(
            "Whether named programs are running, how many instances there are, and how much CPU and memory they use.",
            "Se alcuni programmi sono in esecuzione, quante istanze ci sono e quanta CPU e memoria usano."),
        Icon = "cog",
        Group = ProviderGroup.Software,
        Singleton = false,
        AutoCreate = false,
        Platforms = PlatformSupport.Any,
        Settings =
        [
            Setting.TextList(
                "processes",
                L("Programs", "Programmi"),
                help: L(
                    "Process names, with or without .exe and in any case: chrome, Spotify.exe, eldenring.",
                    "Nomi dei processi, con o senza .exe e in maiuscolo o minuscolo: chrome, Spotify.exe, eldenring."),
                placeholder: "chrome"),
            Setting.Interval(
                2,
                min: 0.5,
                help: L("Seconds between checks. CPU use is the average over this time.", "Secondi tra un controllo e l'altro. L'uso della CPU è la media su questo tempo.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var settings = new ProviderSettings(config.Settings, Descriptor);
        return new ProcessProvider(
            config.Id,
            settings.GetStringList("processes"),
            settings.GetSeconds("interval", 2),
            new SystemProcessSource(),
            ProviderServices.Time(services),
            Environment.ProcessorCount,
            ProviderServices.Logger<ProcessProvider>(services));
    }
}
