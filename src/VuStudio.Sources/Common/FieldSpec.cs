using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Common;

/// <summary>A value extracted for a <see cref="FieldSpec"/>: a number, a text, or neither.</summary>
internal readonly record struct FieldValue(double? Number, string? Text)
{
    public bool HasValue => Number.HasValue || Text is not null;
}

/// <summary>
/// One row of a "fields" table: how to pick a value out of a JSON document (or a script's output) and what
/// sensor to publish it as. Shared by the <c>http-json</c> and <c>script</c> providers, and reusable by any
/// provider that extracts values from JSON.
/// </summary>
internal sealed record FieldSpec
{
    /// <summary>Local sensor id, already normalised.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>A JSONPath-lite expression (see <see cref="JsonPathLite"/>) or, for scripts, an output key.</summary>
    public required string Path { get; init; }

    /// <summary>Canonical unit of the published value (after <see cref="Multiplier"/>).</summary>
    public string Unit { get; init; } = Units.None;

    public SensorCategory Category { get; init; } = SensorCategory.Custom;

    public string? Kind { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double Multiplier { get; init; } = 1;

    /// <summary>Publish the value as text instead of a number.</summary>
    public bool IsText { get; init; }

    public SensorInfo ToSensorInfo(string? device, double? staleAfterSeconds)
    {
        var kind = Kind ?? (IsText ? SensorKinds.Text : SensorHints.KindForUnit(Unit));
        return new SensorInfo
        {
            Id = Id,
            Name = Name,
            Device = device,
            Category = Category,
            Kind = kind,
            Unit = IsText ? Units.None : Unit,
            ValueKind = IsText ? SensorValueKind.Text : SensorValueKind.Number,
            Min = IsText ? null : Min,
            Max = IsText ? null : Max,
            Icon = SensorHints.Icon(Category, kind),
            StaleAfterSeconds = staleAfterSeconds,
        };
    }

    /// <summary>Converts an extracted JSON node into the value to publish; no value when it is missing or not convertible.</summary>
    public FieldValue Convert(JsonNode? node)
    {
        if (node is null)
        {
            return default;
        }

        if (IsText)
        {
            return new FieldValue(null, JsonValues.ToText(node));
        }

        if (!JsonValues.TryGetNumber(node, out var number))
        {
            return default;
        }

        var scaled = number * Multiplier;
        return double.IsFinite(scaled) ? new FieldValue(scaled, null) : default;
    }
}

/// <summary>Schema and parsing for the shared "fields" table.</summary>
internal static class FieldSpecs
{
    /// <summary>The setting key of the table.</summary>
    public const string Key = "fields";

    /// <summary>Columns of the fields table. <paramref name="path"/> describes what the "path" column means for the provider.</summary>
    public static IReadOnlyList<SettingField> Columns(LocalizedText pathLabel, LocalizedText pathHelp) =>
    [
        Setting.Text("id", L("ID", "ID"), null,
            L("Sensor id: lowercase letters, digits, '-' and '/'. Derived from the name when empty.",
              "ID del sensore: lettere minuscole, cifre, '-' e '/'. Se vuoto deriva dal nome.")),
        Setting.Text("name", L("Name", "Nome")),
        Setting.Text("path", pathLabel, null, pathHelp, required: true),
        Setting.Text("unit", L("Unit", "Unità"), null,
            L("Canonical unit of the value after the multiplier: %, °C, MHz, W, V, A, RPM, B, B/s, ms, s.",
              "Unità canonica del valore dopo il moltiplicatore: %, °C, MHz, W, V, A, RPM, B, B/s, ms, s.")),
        Setting.Select("category", L("Category", "Categoria"), SensorOptions.ValueOf(SensorCategory.Custom), SensorOptions.Categories),
        Setting.Text("kind", L("Kind", "Tipo"), null,
            L("Optional: temperature, load, power, fan, throughput, latency, percent, count…",
              "Facoltativo: temperature, load, power, fan, throughput, latency, percent, count…")),
        Setting.Number("min", L("Min", "Min"), null, help: L("Suggested lower bound for gauges.", "Limite inferiore suggerito per gli indicatori.")),
        Setting.Number("max", L("Max", "Max"), null, help: L("Suggested upper bound for gauges.", "Limite superiore suggerito per gli indicatori.")),
        Setting.Number("multiplier", L("Multiplier", "Moltiplicatore"), 1,
            help: L("The value is multiplied by this before publishing (0.001 turns mV into V).",
                    "Il valore viene moltiplicato per questo numero prima di essere pubblicato (0,001 trasforma mV in V).")),
        Setting.Bool("text", L("Text value", "Valore di testo"), false,
            L("Publish the value as text instead of a number.", "Pubblica il valore come testo invece che come numero.")),
    ];

    /// <summary>The whole "fields" table setting.</summary>
    public static SettingField Table(LocalizedText label, LocalizedText pathLabel, LocalizedText pathHelp, LocalizedText? help = null) =>
        Setting.Table(Key, label, Columns(pathLabel, pathHelp), help: help);

    /// <summary>
    /// Reads the fields table. Rows that are unusable (no path, invalid path) are skipped and described in
    /// <paramref name="issues"/>; ids are normalised and made unique.
    /// </summary>
    public static IReadOnlyList<FieldSpec> Parse(ProviderSettings settings, ICollection<string> issues, string key = Key)
    {
        var fields = new List<FieldSpec>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var rowNumber = 0;
        foreach (var row in settings.GetTable(key))
        {
            rowNumber++;
            var path = row.GetString("path");
            if (path is null)
            {
                issues.Add($"Field {rowNumber}: the path is empty.");
                continue;
            }

            if (!JsonPathLite.TryParse(path, out _))
            {
                issues.Add($"Field {rowNumber}: '{path}' is not a valid path.");
                continue;
            }

            var name = row.GetString("name");
            var id = LocalIds.Unique(LocalIds.SlugPath(row.GetString("id") ?? name ?? path), usedIds);
            var multiplier = row.GetDouble("multiplier", 1);
            fields.Add(new FieldSpec
            {
                Id = id,
                Name = name ?? id,
                Path = path,
                Unit = row.GetString("unit") ?? Units.None,
                Category = SensorOptions.ParseCategory(row.GetString("category")),
                Kind = row.GetString("kind"),
                Min = Optional(row, "min"),
                Max = Optional(row, "max"),
                Multiplier = double.IsFinite(multiplier) ? multiplier : 1,
                IsText = row.GetBool("text"),
            });
        }

        return fields;
    }

    private static double? Optional(ProviderSettings row, string key)
    {
        var value = row.GetDouble(key, double.NaN);
        return double.IsFinite(value) ? value : null;
    }
}
