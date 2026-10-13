using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Windows.Lhm;

/// <summary>Creates the LibreHardwareMonitor provider (singleton, id <c>lhm</c>). Constructible on any OS.</summary>
public sealed class LhmProviderFactory : IProviderFactory
{
    public const string TypeId = "lhm";

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = TypeId,
        Name = "LibreHardwareMonitor",
        Description = new LocalizedText(
            "Temperatures, loads, clocks, fans and power of the CPU, GPU, memory, motherboard, drives and network adapters of this PC.",
            "Temperature, carichi, frequenze, ventole e consumi di CPU, GPU, memoria, scheda madre, dischi e schede di rete di questo PC."),
        Icon = "chip",
        Group = ProviderGroup.ThisPc,
        Platforms = PlatformSupport.Windows,
        Singleton = true,
        AutoCreate = true,
        Settings =
        [
            new SettingField
            {
                Key = "interval",
                Label = new LocalizedText("Update interval", "Intervallo di aggiornamento"),
                Kind = SettingKind.Seconds,
                Default = JsonValue.Create(1.0),
                Min = LhmSettings.MinIntervalSeconds,
                Max = LhmSettings.MaxIntervalSeconds,
                Help = new LocalizedText(
                    "How often the sensors are read. Lower values use more CPU.",
                    "Ogni quanto vengono letti i sensori. Valori bassi richiedono più CPU."),
            },
            Toggle("cpu", "CPU", "CPU", true),
            Toggle("gpu", "GPU", "GPU", true),
            Toggle("memory", "Memory", "Memoria", true),
            Toggle("motherboard", "Motherboard and Super I/O", "Scheda madre e Super I/O", true),
            Toggle("controllers", "Fan and cooling controllers", "Controller ventole e raffreddamento", false,
                "Probes serial and USB controllers (T-Balancer, Aquacomputer, NZXT, ...). Off by default because it opens serial ports.",
                "Interroga controller seriali e USB (T-Balancer, Aquacomputer, NZXT, ...). Disattivato di default perché apre porte seriali."),
            Toggle("storage", "Drives (SMART)", "Dischi (SMART)", true),
            Toggle("network", "Network adapters", "Schede di rete", true),
            Toggle("psu", "Power supplies", "Alimentatori", true,
                "Corsair and MSI power supplies connected over USB.",
                "Alimentatori Corsair e MSI collegati via USB."),
            Toggle("battery", "Battery", "Batteria", true),
            new SettingField
            {
                Key = "sensorTypes",
                Label = new LocalizedText("Sensor types (advanced)", "Tipi di sensore (avanzate)"),
                Kind = SettingKind.TextList,
                Default = new JsonArray(),
                Help = new LocalizedText(
                    "Advanced: only publish these LibreHardwareMonitor sensor types, for example Temperature, Load, Fan. Leave empty for all.",
                    "Avanzate: pubblica solo questi tipi di sensore di LibreHardwareMonitor, ad esempio Temperature, Load, Fan. Lascia vuoto per tutti."),
            },
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("LibreHardwareMonitor is only available on Windows.");
        }

        var settings = LhmSettings.From(new ProviderSettings(config.Settings, Descriptor));
        var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance;
        return CreateWindows(config.Id, settings, loggerFactory.CreateLogger("VuStudio.Lhm"));
    }

    // Kept out of Create so that Linux never has to load LibreHardwareMonitor types for this factory.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISensorProvider CreateWindows(string id, LhmSettings settings, ILogger logger) =>
        new LhmProvider(
            id,
            settings,
            new LhmTimings(),
            new LhmEnvironment(ComputerSession.IsElevated, ComputerSession.PawnIoInstalled),
            () => new ComputerSession(settings),
            logger);

    private static SettingField Toggle(string key, string en, string it, bool value, string? helpEn = null, string? helpIt = null) => new()
    {
        Key = key,
        Label = new LocalizedText(en, it),
        Kind = SettingKind.Boolean,
        Default = JsonValue.Create(value),
        Help = helpEn is null ? null : new LocalizedText(helpEn, helpIt),
    };
}
