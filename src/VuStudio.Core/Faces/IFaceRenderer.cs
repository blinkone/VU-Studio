using System.Globalization;
using VuStudio.Core.Lighting;
using VuStudio.Core.Mapping;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;

namespace VuStudio.Core.Faces;

/// <summary>A point of sensor history.</summary>
public readonly record struct HistoryPoint(DateTimeOffset Time, double Value);

/// <summary>Everything a face needs to know about the page it belongs to.</summary>
public sealed class FaceRenderContext : ITemplateResolver
{
    public string Title { get; init; } = "";

    public string Subtitle { get; init; } = "";

    /// <summary>Icon id for <c>{icon}</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>Mapping in display units; drives scale labels.</summary>
    public ValueMapping Mapping { get; init; } = ValueMapping.Percent;

    /// <summary>Current value in display units (null when unknown).</summary>
    public double? Value { get; init; }

    /// <summary>Text for text-valued sensors.</summary>
    public string? ValueText { get; init; }

    /// <summary>Display unit label (e.g. "°C", "Mbit/s").</summary>
    public string Unit { get; init; } = "";

    /// <summary>Fixed decimals for <c>{value}</c>; null = automatic.</summary>
    public int? Decimals { get; init; }

    /// <summary>The page's sensor, for sparklines and bars without an explicit sensor.</summary>
    public string? SensorId { get; init; }

    public NeedleGeometry Geometry { get; init; } = NeedleGeometry.Default;

    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    public Func<string, SensorSnapshot?> Sensors { get; init; } = _ => null;

    public Func<string, TimeSpan, IReadOnlyList<HistoryPoint>> History { get; init; } = (_, _) => [];

    /// <summary>Backlight tint for colored previews (not used for the device bitmap).</summary>
    public RgbwColor? PreviewTint { get; init; }

    public string? Resolve(string name, string? argument, string? format)
    {
        switch (name)
        {
            case "title":
                return Title;
            case "subtitle":
                return Subtitle;
            case "unit":
                return Unit;
            case "icon":
                return Icon ?? "";
            case "value":
                return ValueText ?? (Value is { } v ? FormatNumber(v, format, Decimals) : "–");
            case "min":
                return FormatLabel(Mapping.Min, format);
            case "max":
                return FormatLabel(Mapping.Max, format);
            case "percent":
                return Value is { } p ? Math.Round(Mapping.ToFraction(p) * 100).ToString(Culture) : "–";
            case "time":
                return Now.ToString(string.IsNullOrEmpty(argument) ? "t" : argument, Culture);
            case "date":
                return Now.ToString(string.IsNullOrEmpty(argument) ? "d" : argument, Culture);
            case "sensor":
            case "sensor.value":
            case "sensor.unit":
            case "sensor.name":
                return ResolveSensor(name, argument, format);
            default:
                return null;
        }
    }

    private string ResolveSensor(string name, string? id, string? format)
    {
        if (string.IsNullOrWhiteSpace(id) || Sensors(id.Trim()) is not { } snap)
        {
            return name == "sensor.unit" ? "" : "–";
        }

        if (name == "sensor.name")
        {
            return snap.Info.Name;
        }

        if (snap.Info.ValueKind != SensorValueKind.Number)
        {
            return name == "sensor.unit" ? "" : snap.IsStale ? "–" : snap.Text ?? "–";
        }

        if (snap.Value is not { } value || snap.IsStale)
        {
            return name == "sensor.unit" ? snap.Info.Unit : "–";
        }

        var (number, unit) = UnitConverter.Format(value, snap.Info.Unit, UnitConverter.Auto, null, Culture);
        if (format is not null)
        {
            number = FormatNumber(UnitConverter.Convert(value, snap.Info.Unit, unit), format, null);
        }

        return name switch
        {
            "sensor.value" => number,
            "sensor.unit" => unit,
            _ => string.IsNullOrEmpty(unit) ? number : unit is "%" or "°C" or "°F" ? number + unit : number + " " + unit,
        };
    }

    /// <summary>Formats a scale value: integers without decimals, otherwise automatic precision.</summary>
    public string FormatLabel(double value, string? format = null) =>
        format is null && Math.Abs(value - Math.Round(value)) < 1e-9
            ? Math.Round(value).ToString("0", Culture)
            : FormatNumber(value, format, null);

    private string FormatNumber(double value, string? format, int? decimals)
    {
        if (!string.IsNullOrEmpty(format))
        {
            try
            {
                return value.ToString(format, Culture);
            }
            catch (FormatException)
            {
                // fall through to automatic formatting
            }
        }

        return UnitConverter.FormatNumber(value, decimals, Culture);
    }
}

public sealed record ThemeInfo(
    string Id,
    string Family,
    LocalizedText Name,
    LocalizedText? Description,
    IReadOnlyList<SettingField> Options);

public sealed record FontInfo(string Id, string Family, string Category, IReadOnlyList<int> Weights, bool HasItalic);

public sealed record IconInfo(string Id, string Category, string Path);

/// <summary>Turns face documents into one-bit bitmaps. Implemented by <c>VuStudio.Rendering</c>.</summary>
public interface IFaceRenderer
{
    IReadOnlyList<ThemeInfo> Themes { get; }

    IReadOnlyList<FontInfo> Fonts { get; }

    IReadOnlyList<IconInfo> Icons { get; }

    /// <summary>Returns <see cref="FaceConfig.Custom"/> if set, otherwise generates the document from the theme.</summary>
    FaceDocument BuildDocument(FaceConfig config, FaceRenderContext context);

    MonoBitmap Render(FaceDocument document, FaceRenderContext context);

    /// <summary>Encodes a bitmap as PNG, optionally upscaled and tinted (white paper → tint color) for previews.</summary>
    byte[] EncodePng(MonoBitmap bitmap, int scale = 1, RgbwColor? tint = null);
}

/// <summary>Storage for user-uploaded images referenced by <see cref="ImageLayer.AssetId"/>.</summary>
public interface IAssetStore
{
    byte[]? Get(string assetId);

    /// <summary>Stores the bytes and returns a new asset id.</summary>
    string Add(ReadOnlySpan<byte> content, string? originalName);

    IReadOnlyList<AssetInfo> List();

    bool Delete(string assetId);
}

public sealed record AssetInfo(string Id, string? Name, long Size, DateTimeOffset Created);
