using System.Globalization;
using System.Text.Json.Nodes;
using VuStudio.Core.Faces;
using VuStudio.Core.Mapping;
using VuStudio.Core.Providers;
using VuStudio.Rendering.Text;

namespace VuStudio.Rendering.Themes;

/// <summary>Where the unit goes relative to the value.</summary>
internal enum UnitPlacement
{
    /// <summary>Right of the value, on the same baseline.</summary>
    Baseline,

    /// <summary>Right of the value, top aligned with the value's cap height (superscript style).</summary>
    Top,

    /// <summary>Centred under the value.</summary>
    Below,
}

/// <summary>Describes a "{value}" + "{unit}" block for <see cref="ThemeKit.ValueAndUnit"/>.</summary>
internal sealed record ValueBlock
{
    public double CenterX { get; init; } = 100;

    /// <summary>Baseline of the value text.</summary>
    public double Baseline { get; init; } = 50;

    public string Font { get; init; } = "inter";

    public double Size { get; init; } = 32;

    public int Weight { get; init; } = 700;

    public double LetterSpacing { get; init; }

    public bool Smooth { get; init; } = true;

    public string UnitFont { get; init; } = "inter";

    public double UnitSize { get; init; } = 12;

    public int UnitWeight { get; init; } = 600;

    public bool UnitUppercase { get; init; }

    public double UnitLetterSpacing { get; init; }

    public bool UnitSmooth { get; init; }

    public UnitPlacement Placement { get; init; } = UnitPlacement.Baseline;

    /// <summary>Gap between value and unit (or between value baseline and unit cap top when below).</summary>
    public double Gap { get; init; } = 3;

    /// <summary>Horizontal limits the block must stay within.</summary>
    public double Left { get; init; } = 4;

    public double Right { get; init; } = 196;

    public Ink Ink { get; init; } = Ink.Foreground;
}

/// <summary>Shared building blocks of the built-in themes.</summary>
internal static class ThemeKit
{
    public const double Width = MonoBitmap.DialWidth;
    public const double Height = MonoBitmap.DialHeight;
    public const double CenterX = 100;

    public static ThemeInfo Info(string id, string en, string it, string descriptionEn, string descriptionIt, params SettingField[] options) =>
        new(id, id.Split('/')[0], new LocalizedText(en, it), new LocalizedText(descriptionEn, descriptionIt), options);

    /// <summary>The title template: the configured title (which may itself contain placeholders) or "{title}".</summary>
    public static string TitleTemplate(FaceConfig config) =>
        string.IsNullOrWhiteSpace(config.Title) ? "{title}" : config.Title!;

    public static string ResolvedTitle(FaceConfig config, FaceRenderContext context) =>
        TextTemplate.Render(TitleTemplate(config), context);

    /// <summary>The subtitle template, or null when there is nothing to show.</summary>
    public static string? SubtitleTemplate(FaceConfig config, FaceRenderContext context)
    {
        if (!string.IsNullOrWhiteSpace(config.Subtitle))
        {
            return config.Subtitle;
        }

        return string.IsNullOrWhiteSpace(context.Subtitle) ? null : "{subtitle}";
    }

    /// <summary>The icon template when an icon should be shown and resolves to a bundled icon; otherwise null.</summary>
    public static string? IconTemplate(FaceConfig config, FaceRenderContext context)
    {
        if (!config.ShowIcon)
        {
            return null;
        }

        var template = string.IsNullOrWhiteSpace(config.Icon) ? "{icon}" : config.Icon!.Trim();
        var id = TextTemplate.Render(template, context).Trim();
        return FaceAssets.Shared.Icons.Contains(id) ? template : null;
    }

    /// <summary>A small crisp text layer for the subtitle template.</summary>
    public static TextLayer Subtitle(string template, double x, double y, string font, double size, int weight = 500, TextAlign align = TextAlign.Center, TextBaseline baseline = TextBaseline.Baseline, double maxWidth = 110) => new()
    {
        Name = "Subtitle",
        Text = template,
        X = x,
        Y = y,
        Align = align,
        Baseline = baseline,
        Font = font,
        Size = size,
        Weight = weight,
        MaxWidth = maxWidth,
        Overflow = TextOverflow.Ellipsis,
        Smooth = false,
    };

    public static string Unit(FaceRenderContext context) => TextTemplate.Render("{unit}", context).Trim();

    /// <summary>Warning zone from <see cref="FaceConfig.WarnFrom"/> to the end of the dial, or null.</summary>
    public static ScaleZone? WarnZone(FaceConfig config, ZoneStyle style, double inner, double outer, double to = 1)
    {
        if (config.WarnFrom is not { } from || !double.IsFinite(from) || from >= to)
        {
            return null;
        }

        return new ScaleZone { From = Math.Clamp(from, 0, 1), To = to, Style = style, InnerRadius = inner, OuterRadius = outer };
    }

    public static int LabelCount(FaceConfig config, int min = 2, int max = 12) => Math.Clamp(config.LabelCount, min, max);

    /// <summary>
    /// Minor ticks per major interval so minors fall on round values: steps of 1·10ⁿ get 4 (or 9 when dense),
    /// 2·10ⁿ get 3, 2.5·10ⁿ and 5·10ⁿ get 4 (5·10ⁿ gets 9 when dense). Non-linear scales get a fixed count.
    /// </summary>
    public static int MinorCount(FaceRenderContext context, int majorCount, bool dense)
    {
        var mapping = context.Mapping ?? ValueMapping.Percent;
        if (mapping.Scale != ScaleType.Linear)
        {
            return dense ? 4 : 1;
        }

        var values = mapping.SuggestLabelValues(majorCount);
        if (values.Count < 2)
        {
            return 4;
        }

        // The interior step is the nice one (the ends may be the raw range limits).
        var step = values.Count >= 3 ? values[2] - values[1] : values[1] - values[0];
        if (!(step > 0))
        {
            return 4;
        }

        var mantissa = Math.Round(step / Math.Pow(10, Math.Floor(Math.Log10(step))), 2);
        return mantissa switch
        {
            1 => dense ? 9 : 4,
            2 => 3,
            5 => dense ? 9 : 4,
            _ => 4,
        };
    }

    /// <summary>
    /// A representative value text used to lay out value/unit pairs at build time: the text value for text
    /// sensors, otherwise ~88 % of the larger range end, formatted like <c>{value}</c>.
    /// </summary>
    public static string SampleValue(FaceRenderContext context)
    {
        if (!string.IsNullOrEmpty(context.ValueText))
        {
            return context.ValueText;
        }

        var mapping = context.Mapping ?? ValueMapping.Percent;
        var magnitude = Math.Max(Math.Abs(mapping.Min), Math.Abs(mapping.Max)) * 0.88;
        return UnitConverter.FormatNumber(magnitude, context.Decimals, context.Culture ?? CultureInfo.InvariantCulture);
    }

    /// <summary>Divisor and caption for large scales (e.g. 1000 and "×1000" for 0–8000 RPM).</summary>
    public static (double Divisor, string? Caption) Multiplier(FaceRenderContext context)
    {
        var mapping = context.Mapping ?? ValueMapping.Percent;
        if (mapping.Scale != ScaleType.Linear)
        {
            // Log/custom labels span decades; dividing them would print 0.01, 0.1…
            return (1, null);
        }

        var max = Math.Max(Math.Abs(mapping.Min), Math.Abs(mapping.Max));
        return max switch
        {
            >= 2e9 => (1e9, "×10⁹"),
            >= 2e6 => (1e6, "×10⁶"),
            >= 2000 => (1000, "×1000"),
            _ => (1, null),
        };
    }

    public static float Measure(string text, string font, double size, int weight, bool italic = false, double letterSpacing = 0, bool smooth = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        using var shaped = TextShaper.Shape(FaceAssets.Shared.Fonts, text, new TextStyle(font, (float)size, weight, italic, smooth, (float)letterSpacing));
        return shaped.Width;
    }

    /// <summary>
    /// Size and overflow for a static text (e.g. the title) that should fit <paramref name="maxWidth"/>:
    /// shrink down to <paramref name="minSize"/>, then ellipsize at that size instead of becoming unreadable.
    /// </summary>
    public static (double Size, TextOverflow Overflow) Fit(string text, string font, double size, int weight, double maxWidth, double minSize, bool italic = false, double letterSpacing = 0)
    {
        var width = Measure(text, font, size, weight, italic, letterSpacing);
        if (width <= maxWidth)
        {
            return (size, TextOverflow.Shrink);
        }

        var fitted = Math.Floor(size * maxWidth / width * 2) / 2;
        return fitted >= minSize ? (fitted, TextOverflow.Shrink) : (minSize, TextOverflow.Ellipsis);
    }

    public static float CapHeight(string font, double size, int weight)
    {
        using var shaped = TextShaper.Shape(FaceAssets.Shared.Fonts, "H", new TextStyle(font, (float)size, weight));
        return shaped.CapHeight;
    }

    /// <summary>
    /// "{value}" and "{unit}" laid out as a pair centred on <see cref="ValueBlock.CenterX"/> for a typical
    /// value. The value is right-aligned on a fixed anchor so the unit never moves; longer values grow to the
    /// left and shrink when they reach the block's left limit. When even a typical value plus unit is too
    /// wide, the value size is reduced up front.
    /// </summary>
    public static List<FaceLayer> ValueAndUnit(FaceRenderContext context, ValueBlock block)
    {
        var unit = Unit(context);
        var unitText = block.UnitUppercase ? unit.ToUpper(context.Culture ?? CultureInfo.InvariantCulture) : unit;
        var sample = SampleValue(context);
        var beside = unit.Length > 0 && block.Placement != UnitPlacement.Below;
        var unitWidth = Measure(unitText, block.UnitFont, block.UnitSize, block.UnitWeight, letterSpacing: block.UnitLetterSpacing, smooth: block.UnitSmooth);
        var available = block.Right - block.Left - (beside ? block.Gap + unitWidth : 0);
        var size = block.Size;
        var valueWidth = Measure(sample, block.Font, size, block.Weight, letterSpacing: block.LetterSpacing, smooth: block.Smooth);
        if (valueWidth > available && valueWidth > 0)
        {
            size = Math.Max(Math.Round(block.Size * 0.45), Math.Floor(block.Size * available / valueWidth));
            if (FaceAssets.Shared.Fonts.FindFamily(block.Font) is { IsPixel: true, PixelGrid: > 0 } pixel)
            {
                // Pixel fonts only stay crisp at whole multiples of their grid.
                size = Math.Max(pixel.PixelGrid, Math.Floor(size / pixel.PixelGrid) * pixel.PixelGrid);
            }

            valueWidth = Measure(sample, block.Font, size, block.Weight, letterSpacing: block.LetterSpacing * size / block.Size, smooth: block.Smooth);
        }

        var letterSpacing = block.LetterSpacing * size / block.Size;
        var layers = new List<FaceLayer>();

        TextLayer ValueLayer(double x, TextAlign align, double maxWidth) => new()
        {
            Name = "Value",
            Text = "{value}",
            X = x,
            Y = block.Baseline,
            Font = block.Font,
            Size = size,
            Weight = block.Weight,
            Align = align,
            Baseline = TextBaseline.Baseline,
            MaxWidth = Math.Max(10, maxWidth),
            Overflow = TextOverflow.Shrink,
            LetterSpacing = letterSpacing,
            Smooth = block.Smooth,
            Ink = block.Ink,
        };

        TextLayer UnitLayer(double x, double y, TextAlign align, TextBaseline baseline, double maxWidth) => new()
        {
            Name = "Unit",
            Text = "{unit}",
            X = x,
            Y = y,
            Font = block.UnitFont,
            Size = block.UnitSize,
            Weight = block.UnitWeight,
            Align = align,
            Baseline = baseline,
            Uppercase = block.UnitUppercase,
            LetterSpacing = block.UnitLetterSpacing,
            MaxWidth = Math.Max(10, maxWidth),
            Overflow = TextOverflow.Shrink,
            Smooth = block.UnitSmooth,
            Ink = block.Ink,
        };

        if (!beside)
        {
            var half = Math.Min(block.CenterX - block.Left, block.Right - block.CenterX);
            layers.Add(ValueLayer(block.CenterX, TextAlign.Center, 2 * half));
            if (unit.Length > 0)
            {
                var unitCap = CapHeight(block.UnitFont, block.UnitSize, block.UnitWeight);
                layers.Add(UnitLayer(block.CenterX, block.Baseline + block.Gap + unitCap, TextAlign.Center, TextBaseline.Baseline, 2 * half));
            }

            return layers;
        }

        var total = valueWidth + block.Gap + unitWidth;
        var start = Math.Max(block.Left, Math.Min(block.CenterX - (total / 2), block.Right - total));
        var anchor = Math.Round(start + valueWidth);
        layers.Add(ValueLayer(anchor, TextAlign.Right, anchor - block.Left));
        var unitX = anchor + block.Gap;
        if (block.Placement == UnitPlacement.Top)
        {
            var capTop = block.Baseline - CapHeight(block.Font, size, block.Weight);
            layers.Add(UnitLayer(unitX, capTop, TextAlign.Left, TextBaseline.Top, block.Right - unitX));
        }
        else
        {
            layers.Add(UnitLayer(unitX, block.Baseline, TextAlign.Left, TextBaseline.Baseline, block.Right - unitX));
        }

        return layers;
    }

    /// <summary>A radial line along the needle direction at <paramref name="fraction"/>, from radius <paramref name="inner"/> to <paramref name="outer"/>.</summary>
    public static ShapeLayer RadialLine(NeedleGeometry geometry, double fraction, double inner, double outer, double width, string name = "Tick")
    {
        var (x1, y1) = geometry.PointAt(fraction, inner);
        var (x2, y2) = geometry.PointAt(fraction, outer);
        return Line(x1, y1, x2, y2, width, name);
    }

    /// <summary>Text centred on the point at <paramref name="radius"/> along the needle direction at <paramref name="fraction"/>.</summary>
    public static TextLayer ArcText(NeedleGeometry geometry, double fraction, double radius, string text, string font, double size, int weight, bool smooth = false, string name = "Label")
    {
        var (x, y) = geometry.PointAt(fraction, radius);
        return new TextLayer
        {
            Name = name,
            Text = text,
            X = Math.Round(x * 2) / 2,
            Y = Math.Round(y * 2) / 2,
            Font = font,
            Size = size,
            Weight = weight,
            Align = TextAlign.Center,
            Baseline = TextBaseline.Middle,
            Overflow = TextOverflow.Visible,
            Smooth = smooth,
        };
    }

    /// <summary>An arc around the needle pivot between two dial fractions, as a shape layer.</summary>
    public static ShapeLayer PivotArc(NeedleGeometry geometry, double from, double to, double radius, double width, string name = "Arc") => new()
    {
        Name = name,
        Kind = ShapeKind.Arc,
        X = geometry.PivotX - radius,
        Y = geometry.PivotY - radius,
        Width = 2 * radius,
        Height = 2 * radius,
        StartAngle = geometry.AngleAt(from) - 90,
        SweepAngle = geometry.AngleAt(to) - geometry.AngleAt(from),
        StrokeWidth = width,
    };

    /// <summary>A sparkline of the page's sensor.</summary>
    public static SparklineLayer Sparkline(double x, double y, double width, double height, SparklineStyle style = SparklineStyle.Line, double stroke = 1.5, bool border = false) => new()
    {
        Name = "History",
        X = x,
        Y = y,
        Width = width,
        Height = height,
        Style = style,
        StrokeWidth = stroke,
        Border = border,
        WindowSeconds = 300,
    };

    public static ShapeLayer Line(double x1, double y1, double x2, double y2, double width = 1, string? name = null, bool dashed = false) => new()
    {
        Name = name ?? "Line",
        Kind = ShapeKind.Line,
        X = x1,
        Y = y1,
        X2 = x2,
        Y2 = y2,
        StrokeWidth = width,
        Dashed = dashed,
    };

    public static ShapeLayer Rect(double x, double y, double width, double height, double stroke = 1, bool fill = false, double radius = 0, Ink ink = Ink.Foreground, string? name = null) => new()
    {
        Name = name ?? "Box",
        Kind = ShapeKind.Rectangle,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        StrokeWidth = stroke,
        Stroke = stroke > 0,
        Fill = fill,
        CornerRadius = radius,
        Ink = ink,
    };

    // ---- options -----------------------------------------------------------------------------------

    public static bool Option(FaceConfig config, string key, bool fallback) =>
        Node(config, key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b
        : Node(config, key) is JsonValue s && s.TryGetValue<string>(out var str) && bool.TryParse(str, out var parsed) ? parsed
        : fallback;

    public static string Option(FaceConfig config, string key, string fallback) =>
        Node(config, key) is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : fallback;

    public static double Option(FaceConfig config, string key, double fallback)
    {
        if (Node(config, key) is not JsonValue v)
        {
            return fallback;
        }

        if (v.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            return d;
        }

        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p) ? p : fallback;
    }

    public static SettingField BoolOption(string key, string en, string it, bool defaultValue, string? helpEn = null, string? helpIt = null) => new()
    {
        Key = key,
        Label = new LocalizedText(en, it),
        Kind = SettingKind.Boolean,
        Default = JsonValue.Create(defaultValue),
        Help = helpEn is null ? null : new LocalizedText(helpEn, helpIt),
    };

    public static SettingField TextOption(string key, string en, string it, string defaultValue, string? helpEn = null, string? helpIt = null) => new()
    {
        Key = key,
        Label = new LocalizedText(en, it),
        Kind = SettingKind.Text,
        Default = JsonValue.Create(defaultValue),
        Help = helpEn is null ? null : new LocalizedText(helpEn, helpIt),
    };

    public static SettingField SelectOption(string key, string en, string it, string defaultValue, params SettingOption[] options) => new()
    {
        Key = key,
        Label = new LocalizedText(en, it),
        Kind = SettingKind.Select,
        Default = JsonValue.Create(defaultValue),
        Options = options,
    };

    private static JsonNode? Node(FaceConfig config, string key)
    {
        if (config.Options is not { } options)
        {
            return null;
        }

        if (options.TryGetPropertyValue(key, out var node))
        {
            return node;
        }

        // Tolerate other casings from hand-edited JSON.
        foreach (var (name, value) in options)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
