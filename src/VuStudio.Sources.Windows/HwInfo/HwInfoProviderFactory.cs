using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Sources.Windows.HwInfo;

/// <summary>Creates the HWiNFO shared-memory provider (singleton, id <c>hwinfo</c>). Constructible on any OS.</summary>
public sealed class HwInfoProviderFactory : IProviderFactory
{
    public const string TypeId = "hwinfo";

    public ProviderDescriptor Descriptor { get; } = new()
    {
        Type = TypeId,
        Name = "HWiNFO",
        Description = new LocalizedText(
            "Sensors from HWiNFO's shared memory. HWiNFO must be running with the Sensors window started and Shared Memory Support enabled; the free edition turns it off after 12 hours.",
            "Sensori dalla memoria condivisa di HWiNFO. HWiNFO deve essere in esecuzione con la finestra Sensori avviata e il supporto alla memoria condivisa attivo; l'edizione gratuita lo disattiva dopo 12 ore."),
        Icon = "monitor-dashboard",
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
                Min = 0.25,
                Max = 60,
                Help = new LocalizedText(
                    "How often the shared memory is read. HWiNFO itself publishes every 2 s by default.",
                    "Ogni quanto viene letta la memoria condivisa. HWiNFO la aggiorna di default ogni 2 s."),
            },
            new SettingField
            {
                Key = "include",
                Label = new LocalizedText("Only include sensors containing", "Includi solo i sensori che contengono"),
                Kind = SettingKind.TextList,
                Default = new JsonArray(),
                Help = new LocalizedText(
                    "Text matched against the sensor group and reading names, for example CPU or Drive. Leave empty to include everything.",
                    "Testo cercato nel nome del gruppo di sensori e della lettura, ad esempio CPU o Drive. Lascia vuoto per includere tutto."),
            },
            new SettingField
            {
                Key = "skipDuplicates",
                Label = new LocalizedText("Skip duplicate sensors", "Ignora i sensori duplicati"),
                Kind = SettingKind.Boolean,
                Default = JsonValue.Create(true),
                Help = new LocalizedText(
                    "HWiNFO can list a reading twice with the same identifiers. Keep only the first one.",
                    "HWiNFO può elencare due volte una lettura con gli stessi identificatori. Mantiene solo la prima."),
            },
        ],
    };

    public ISensorProvider Create(ProviderInstanceConfig config, IServiceProvider services)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HWiNFO shared memory is only available on Windows.");
        }

        var settings = HwInfoSettings.From(new ProviderSettings(config.Settings, Descriptor));
        var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? NullLoggerFactory.Instance;
        var time = services.GetService(typeof(TimeProvider)) as TimeProvider;
        return CreateWindows(config.Id, settings, loggerFactory.CreateLogger("VuStudio.HwInfo"), time);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISensorProvider CreateWindows(string id, HwInfoSettings settings, ILogger logger, TimeProvider? time) =>
        new HwInfoProvider(id, settings, HwInfoWindowsMemory.Open, HwInfoWindowsMemory.AnsiEncoding(), logger, time);
}
