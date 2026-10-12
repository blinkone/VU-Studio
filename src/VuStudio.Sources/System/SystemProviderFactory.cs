using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;
using VuStudio.Sources.SystemMetrics.Linux;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.SystemMetrics;

/// <summary>
/// Creates the <c>system</c> provider: baseline operating-system metrics that work without extra software.
/// On Windows it complements LibreHardwareMonitor; on Linux it is the main source and powers the remote agent.
/// </summary>
public sealed class SystemProviderFactory : IProviderFactory
{
    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = "system",
        Name = L("System", "Sistema"),
        Description = L(
            "CPU, memory, disks, network, temperatures, fans, batteries and uptime, read from the operating system itself.",
            "CPU, memoria, dischi, rete, temperature, ventole, batterie e tempo di attività, letti direttamente dal sistema operativo."),
        Icon = "chip",
        Group = ProviderGroup.ThisPc,
        Singleton = true,
        AutoCreate = true,
        Platforms = PlatformSupport.Any,
        Settings =
        [
            Setting.Interval(1, min: 0.25, max: 60, help: L("Seconds between readings.", "Secondi tra una lettura e l'altra.")),
            Setting.Bool(
                "perCore",
                L("Per-core CPU sensors", "Sensori CPU per core"),
                true,
                L(
                    "Publish load and clock speed of every CPU core (Linux). Turn off on machines with very many cores.",
                    "Pubblica carico e frequenza di ogni core della CPU (Linux). Disattiva sulle macchine con moltissimi core.")),
            Setting.Bool(
                "nvidiaSmi",
                L("NVIDIA GPUs (nvidia-smi)", "GPU NVIDIA (nvidia-smi)"),
                true,
                L(
                    "Read NVIDIA GPUs with the nvidia-smi tool when it is installed. It is queried every two seconds at most.",
                    "Legge le GPU NVIDIA con lo strumento nvidia-smi, se installato. Viene interrogato al massimo ogni due secondi.")),
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        var settings = new ProviderSettings(config.Settings, Descriptor);
        return new SystemProvider(
            config.Id,
            BuildCollectors(settings.GetBool("perCore", true), settings.GetBool("nvidiaSmi", true)),
            settings.GetSeconds("interval", 1),
            ProviderServices.Time(services),
            ProviderServices.Logger<SystemProvider>(services));
    }

    /// <summary>The collectors that make sense on the machine this runs on.</summary>
    internal static List<ISystemCollector> BuildCollectors(bool perCore, bool nvidia)
    {
        var collectors = new List<ISystemCollector>
        {
            new NetworkCollector(new SystemNetworkInterfaceReader()),
            new DiskUsageCollector(new SystemDriveReader()),
        };

        if (OperatingSystem.IsLinux())
        {
            collectors.Add(new LinuxCollector("/", perCore));
        }
        else
        {
            collectors.Add(new HostCollector());
            if (OperatingSystem.IsWindows())
            {
                collectors.Add(new WindowsCollector(new Win32Api()));
            }
        }

        if (nvidia && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
        {
            collectors.Add(new NvidiaSmiCollector());
        }

        return collectors;
    }
}
