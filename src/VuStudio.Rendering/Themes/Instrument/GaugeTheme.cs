using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Instrument;

/// <summary>
/// instrument/gauge — an automotive cluster dial: dense ticks with heavy majors, B612 Bold numerals inside the
/// arc, a hatched red line zone and, for large ranges, numerals divided by 1000 with a "×1000" caption.
/// </summary>
internal sealed class GaugeTheme : IThemeGenerator
{
    private const double Radius = 150;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "instrument/gauge",
        "Gauge",
        "Strumento auto",
        "An automotive dial: dense ticks, bold numerals, a hatched red line and a ×1000 multiplier for big ranges.",
        "Un quadrante automobilistico: tacche fitte, numeri in grassetto, zona rossa tratteggiata e moltiplicatore ×1000.",
        ThemeKit.BoolOption("multiplier", "×1000 numerals for large ranges", "Numeri ×1000 per scale ampie", true),
        ThemeKit.BoolOption("readout", "Digital readout", "Lettura digitale", true));

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var majors = ThemeKit.LabelCount(config);
        var (divisor, multiplier) = ThemeKit.Option(config, "multiplier", true) ? ThemeKit.Multiplier(context) : (1, null);

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = Radius,
            ShowArc = true,
            ArcRadius = Radius - 0.5,
            ArcWidth = 2,
            MajorCount = majors,
            MinorPerMajor = ThemeKit.MinorCount(context, majors, dense: true),
            MajorLength = 13,
            MajorWidth = 3,
            MinorLength = 6,
            MinorWidth = 1,
            LabelRadius = 128,
            LabelFont = "b612",
            LabelSize = 12,
            LabelWeight = 700,
            LabelDivisor = divisor,
        };
        if (ThemeKit.WarnZone(config, ZoneStyle.Hatched, Radius - 13, Radius) is { } warn)
        {
            scale.Zones.Add(warn);
        }

        layers.Add(scale);

        // Unit (with the multiplier) printed inside the dial, like "×1000 r/min" on a tachometer.
        var unit = ThemeKit.Unit(context);
        if (multiplier is not null || unit.Length > 0)
        {
            var caption = multiplier is null ? "{unit}" : unit.Length > 0 ? multiplier + " {unit}" : multiplier;
            layers.Add(new TextLayer
            {
                Name = "Caption",
                Text = caption,
                X = ThemeKit.CenterX,
                Y = 113,
                Baseline = TextBaseline.Middle,
                Font = "b612",
                Size = 9,
                Weight = 400,
                MaxWidth = 70,
                Overflow = TextOverflow.Shrink,
                Smooth = false,
            });
        }

        // Title row and digital readout in the dome.
        var icon = ThemeKit.IconTemplate(config, context);
        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = ThemeKit.CenterX,
            Y = 6,
            Baseline = TextBaseline.Top,
            Font = "b612",
            Size = 10,
            Weight = 700,
            Uppercase = true,
            LetterSpacing = 1.5,
            MaxWidth = icon is null ? 180 : 140,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });

        if (icon is not null)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = 14, Y = 14, Size = 18 });
        }

        if (config.ShowValue && ThemeKit.Option(config, "readout", true))
        {
            layers.AddRange(ThemeKit.ValueAndUnit(context, new ValueBlock
            {
                Baseline = config.ShowSparkline ? 41 : 48,
                Font = "b612",
                Size = 26,
                Weight = 700,
                UnitFont = "b612",
                UnitSize = 10,
                UnitWeight = 400,
                Placement = UnitPlacement.Baseline,
                Gap = 4,
                Left = 20,
                Right = 180,
            }));
        }

        if (config.ShowSparkline)
        {
            layers.Add(ThemeKit.Sparkline(62, 50, 76, 14, SparklineStyle.Bars));
        }

        if (ThemeKit.SubtitleTemplate(config, context) is { } subtitle)
        {
            layers.Add(ThemeKit.Subtitle(subtitle, ThemeKit.CenterX, 126, "b612", 9, 400, baseline: TextBaseline.Middle, maxWidth: 96));
        }

        return doc;
    }
}
