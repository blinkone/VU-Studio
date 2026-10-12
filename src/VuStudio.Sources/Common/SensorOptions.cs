using System.Text.Json;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Common;

/// <summary>Select options and parsing for <see cref="SensorCategory"/>, shared by every provider that lets the user classify sensors.</summary>
internal static class SensorOptions
{
    /// <summary>One option per <see cref="SensorCategory"/>; the value is the camelCase enum name, as in the JSON config.</summary>
    public static IReadOnlyList<SettingOption> Categories { get; } =
        Enum.GetValues<SensorCategory>().Select(c => new SettingOption(ValueOf(c), Label(c))).ToList();

    public static string ValueOf(SensorCategory category) => JsonNamingPolicy.CamelCase.ConvertName(category.ToString());

    /// <summary>Parses a category name (any casing); returns <paramref name="fallback"/> when unknown or empty.</summary>
    public static SensorCategory ParseCategory(string? text, SensorCategory fallback = SensorCategory.Custom) =>
        Enum.TryParse<SensorCategory>(text, ignoreCase: true, out var category) && Enum.IsDefined(category)
            ? category
            : fallback;

    private static LocalizedText Label(SensorCategory category) => category switch
    {
        SensorCategory.Cpu => L("CPU", "CPU"),
        SensorCategory.Gpu => L("GPU", "GPU"),
        SensorCategory.Memory => L("Memory", "Memoria"),
        SensorCategory.Storage => L("Storage", "Archiviazione"),
        SensorCategory.Network => L("Network", "Rete"),
        SensorCategory.Cooling => L("Cooling", "Raffreddamento"),
        SensorCategory.Power => L("Power", "Alimentazione"),
        SensorCategory.Battery => L("Battery", "Batteria"),
        SensorCategory.Motherboard => L("Motherboard", "Scheda madre"),
        SensorCategory.Audio => L("Audio", "Audio"),
        SensorCategory.Media => L("Media", "Multimedia"),
        SensorCategory.Game => L("Games", "Giochi"),
        SensorCategory.Process => L("Processes", "Processi"),
        SensorCategory.Time => L("Time", "Tempo"),
        SensorCategory.Weather => L("Weather", "Meteo"),
        SensorCategory.Finance => L("Finance", "Finanza"),
        SensorCategory.Home => L("Home", "Casa"),
        SensorCategory.System => L("System", "Sistema"),
        SensorCategory.Custom => L("Custom", "Personalizzato"),
        _ => L(category.ToString(), category.ToString()),
    };
}
