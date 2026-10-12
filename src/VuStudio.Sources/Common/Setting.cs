using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using static VuStudio.Sources.Common.Localized;

namespace VuStudio.Sources.Common;

/// <summary>
/// Builders for <see cref="SettingField"/>s so that provider descriptors stay short and the
/// wording of common settings (interval, timeout…) is identical everywhere.
/// </summary>
internal static class Setting
{
    public static SettingField Text(
        string key,
        LocalizedText label,
        string? @default = null,
        LocalizedText? help = null,
        string? placeholder = null,
        bool required = false,
        SettingKind kind = SettingKind.Text) => new()
        {
            Key = key,
            Label = label,
            Kind = kind,
            Default = @default is null ? null : JsonValue.Create(@default),
            Help = help,
            Placeholder = placeholder,
            Required = required,
        };

    public static SettingField Url(string key, LocalizedText label, string? @default = null, LocalizedText? help = null, string? placeholder = null, bool required = false) =>
        Text(key, label, @default, help, placeholder, required, SettingKind.Url);

    public static SettingField Secret(string key, LocalizedText label, LocalizedText? help = null) =>
        Text(key, label, null, help, null, false, SettingKind.Secret);

    public static SettingField Multiline(string key, LocalizedText label, string? @default = null, LocalizedText? help = null, string? placeholder = null, bool required = false) =>
        Text(key, label, @default, help, placeholder, required, SettingKind.Multiline);

    public static SettingField Number(string key, LocalizedText label, double? @default, double? min = null, double? max = null, LocalizedText? help = null) => new()
    {
        Key = key,
        Label = label,
        Kind = SettingKind.Number,
        Default = @default is { } value ? JsonValue.Create(value) : null,
        Min = min,
        Max = max,
        Help = help,
    };

    public static SettingField Seconds(string key, LocalizedText label, double @default, double min = 0.05, double max = 86_400, LocalizedText? help = null) => new()
    {
        Key = key,
        Label = label,
        Kind = SettingKind.Seconds,
        Default = JsonValue.Create(@default),
        Min = min,
        Max = max,
        Help = help,
    };

    public static SettingField Bool(string key, LocalizedText label, bool @default, LocalizedText? help = null) => new()
    {
        Key = key,
        Label = label,
        Kind = SettingKind.Boolean,
        Default = JsonValue.Create(@default),
        Help = help,
    };

    public static SettingField Select(string key, LocalizedText label, string @default, IReadOnlyList<SettingOption> options, LocalizedText? help = null) => new()
    {
        Key = key,
        Label = label,
        Kind = SettingKind.Select,
        Default = JsonValue.Create(@default),
        Options = options,
        Help = help,
    };

    public static SettingField TextList(string key, LocalizedText label, IEnumerable<string>? @default = null, LocalizedText? help = null, string? placeholder = null) => new()
    {
        Key = key,
        Label = label,
        Kind = SettingKind.TextList,
        Default = new JsonArray((@default ?? []).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        Help = help,
        Placeholder = placeholder,
    };

    public static SettingField Table(string key, LocalizedText label, IReadOnlyList<SettingField> columns, JsonArray? @default = null, LocalizedText? help = null) => new()
    {
        Key = key,
        Label = label,
        Kind = SettingKind.Table,
        Columns = columns,
        Default = @default ?? new JsonArray(),
        Help = help,
    };

    public static SettingOption Option(string value, string en, string it) => new(value, L(en, it));

    /// <summary>The standard "update interval" field shared by every polling provider.</summary>
    public static SettingField Interval(double defaultSeconds, double min = 0.1, double max = 86_400, LocalizedText? help = null) => Seconds(
        "interval",
        L("Update interval", "Intervallo di aggiornamento"),
        defaultSeconds,
        min,
        max,
        help ?? L("How often the source is read, in seconds.", "Ogni quanti secondi viene letta la sorgente."));

    /// <summary>Builds a JSON object row for the default value of a <see cref="SettingKind.Table"/> field.</summary>
    public static JsonObject Row(params (string Key, JsonNode? Value)[] cells)
    {
        var row = new JsonObject();
        foreach (var (key, value) in cells)
        {
            row[key] = value;
        }

        return row;
    }
}
