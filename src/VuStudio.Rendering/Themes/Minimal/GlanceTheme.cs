using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Minimal;

/// <summary>
/// minimal/glance — a "quick glance" face: a large icon beside a header ("Title · unit") and a big
/// left-aligned value, over a quiet scale. Without an icon the title and value are centred instead.
/// </summary>
internal sealed class GlanceTheme : IThemeGenerator
{
    private const double ArcRadius = 148;
    private const double IconSize = 48;
    private const double ColumnLeft = 66;
    private const double ColumnRight = 194;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "minimal/glance",
        "Glance",
        "Colpo d'occhio",
        "A large icon next to the title and value, readable from across the desk.",
        "Una grande icona accanto a titolo e valore, leggibile anche da lontano.");

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var icon = ThemeKit.IconTemplate(config, context);
        var sparkline = config.ShowSparkline;
        var top = sparkline ? 8.0 : 13.0;

        if (icon is null)
        {
            CenteredHeader(layers, config, context, top);
        }
        else
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = 8 + (IconSize / 2), Y = top + (IconSize / 2) - 3, Size = IconSize });
            Column(layers, config, context, top);
        }

        if (sparkline)
        {
            layers.Add(ThemeKit.Sparkline(icon is null ? 50 : ColumnLeft, 57, icon is null ? 100 : ColumnRight - ColumnLeft - 6, 13, stroke: 1));
        }

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = ArcRadius + 0.5,
            ShowArc = true,
            ArcRadius = ArcRadius,
            ArcWidth = 2,
            EvenSpacing = true,
            MajorCount = ThemeKit.LabelCount(config),
            MinorPerMajor = 0,
            MajorLength = 7,
            MajorWidth = 2,
            ShowLabels = false,
        };
        if (ThemeKit.WarnZone(config, ZoneStyle.Band, ArcRadius - 1, ArcRadius + 4) is { } warn)
        {
            scale.Zones.Add(warn);
        }

        layers.Add(scale);
        layers.Add(new ScaleLayer
        {
            Name = "Range",
            Radius = ArcRadius,
            EvenSpacing = true,
            MajorCount = 2,
            MinorPerMajor = 0,
            MajorLength = 0,
            MinorLength = 0,
            LabelRadius = ArcRadius - 15,
            LabelFont = "inter",
            LabelSize = 10,
            LabelWeight = 600,
        });

        if (ThemeKit.SubtitleTemplate(config, context) is { } subtitle)
        {
            layers.Add(new TextLayer
            {
                Name = "Subtitle",
                Text = subtitle,
                X = ThemeKit.CenterX,
                Y = 136,
                Font = "inter",
                Size = 9,
                Weight = 500,
                Baseline = TextBaseline.Baseline,
                MaxWidth = 100,
                Overflow = TextOverflow.Ellipsis,
                Smooth = false,
            });
        }

        return doc;
    }

    /// <summary>Header "Title  unit" and the value, left-aligned in the column right of the icon.</summary>
    private static void Column(List<FaceLayer> layers, FaceConfig config, FaceRenderContext context, double top)
    {
        var width = ColumnRight - ColumnLeft;
        var unit = ThemeKit.Unit(context);
        var unitWidth = unit.Length > 0 ? ThemeKit.Measure(unit, "inter", 12, 500, smooth: false) : 0;
        var titleMax = unit.Length > 0 ? width - unitWidth - 6 : width;
        var title = ThemeKit.ResolvedTitle(config, context);
        var titleWidth = Math.Min(titleMax, ThemeKit.Measure(title, "inter", 13, 700, smooth: false));

        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = ColumnLeft,
            Y = top,
            Align = TextAlign.Left,
            Baseline = TextBaseline.Top,
            Font = "inter",
            Size = 13,
            Weight = 700,
            MaxWidth = titleMax,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });

        if (unit.Length > 0)
        {
            layers.Add(new TextLayer
            {
                Name = "Unit",
                Text = "{unit}",
                X = Math.Round(ColumnLeft + titleWidth + 6),
                Y = top + ThemeKit.CapHeight("inter", 13, 700),
                Align = TextAlign.Left,
                Baseline = TextBaseline.Baseline,
                Font = "inter",
                Size = 12,
                Weight = 500,
                MaxWidth = unitWidth + 2,
                Overflow = TextOverflow.Shrink,
                Smooth = false,
            });
        }

        if (config.ShowValue)
        {
            // Size the digits so a typical value fills the column without shrinking at run time.
            var sample = ThemeKit.SampleValue(context);
            var size = 34.0;
            var sampleWidth = ThemeKit.Measure(sample, "inter", size, 700, letterSpacing: -0.5);
            if (sampleWidth > width)
            {
                size = Math.Max(16, Math.Floor(size * width / sampleWidth));
            }

            layers.Add(new TextLayer
            {
                Name = "Value",
                Text = "{value}",
                X = ColumnLeft - 1,
                Y = top + 47,
                Align = TextAlign.Left,
                Baseline = TextBaseline.Baseline,
                Font = "inter",
                Size = size,
                Weight = 700,
                LetterSpacing = -0.5,
                MaxWidth = width + 1,
                Overflow = TextOverflow.Shrink,
            });
        }
    }

    private static void CenteredHeader(List<FaceLayer> layers, FaceConfig config, FaceRenderContext context, double top)
    {
        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = ThemeKit.CenterX,
            Y = top,
            Align = TextAlign.Center,
            Baseline = TextBaseline.Top,
            Font = "inter",
            Size = 13,
            Weight = 700,
            MaxWidth = 176,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });

        if (config.ShowValue)
        {
            layers.AddRange(ThemeKit.ValueAndUnit(context, new ValueBlock
            {
                Baseline = top + 47,
                Size = 34,
                Weight = 700,
                LetterSpacing = -0.5,
                UnitSize = 13,
                UnitWeight = 600,
                Placement = UnitPlacement.Baseline,
                Gap = 4,
                Left = 12,
                Right = 188,
            }));
        }
    }
}
