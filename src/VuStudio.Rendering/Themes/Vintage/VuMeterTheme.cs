using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Vintage;

/// <summary>
/// vintage/vu — a real VU meter scale. The dB marks sit at fraction 10^(dB/20) / 10^(3/20), i.e. the page
/// is expected to deliver a linear amplitude 0–100 (+3 VU at full scale, 0 VU at ≈ 70.8 %). A thick band
/// marks 0…+3, a small percent scale (100 % = 0 VU) runs underneath, and a big "VU" legend sits on top.
/// The marks do not follow the page mapping, so they are drawn as line and text layers rather than a
/// <see cref="ScaleLayer"/> (which can only place majors on mapping values or evenly).
/// </summary>
internal sealed class VuMeterTheme : IThemeGenerator
{
    private const double ArcRadius = 146;
    private const double PercentRadius = 114;

    /// <summary>Labelled marks; signs only at the ends, like classic VU meters.</summary>
    private static readonly (double Db, string Label)[] Majors =
    [
        (-20, "−20"), (-10, "10"), (-7, "7"), (-5, "5"), (-3, "3"), (-2, "2"), (-1, "1"),
        (0, "0"), (1, "1"), (2, "2"), (3, "+3"),
    ];

    private static readonly double[] Minors = [-15, -6, -4, -0.5, 0.5, 1.5, 2.5];

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "vintage/vu",
        "VU meter",
        "VU meter",
        "A studio VU meter scale (−20…+3 VU) for linear audio levels 0–100, with a percent scale and a red zone from 0 VU.",
        "Una scala VU da studio (−20…+3 VU) per livelli audio lineari 0–100, con scala percentuale e zona rossa da 0 VU.",
        ThemeKit.TextOption("legend", "Legend", "Legenda", "VU"),
        ThemeKit.BoolOption("percentScale", "Percent scale", "Scala percentuale", true));

    /// <summary>Dial fraction of a VU reading for a linear amplitude input where +3 VU is full scale.</summary>
    public static double Fraction(double db) => Math.Pow(10, db / 20) / Math.Pow(10, 3 / 20.0);

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var g = context.Geometry ?? NeedleGeometry.Default;
        var zeroVu = Fraction(0);

        // Arc and the 0…+3 band (a scale layer without ticks keeps them editable as one unit).
        var arc = new ScaleLayer
        {
            Name = "Arc",
            Radius = ArcRadius,
            ShowArc = true,
            ArcWidth = 1,
            MajorLength = 0,
            MinorLength = 0,
            MinorPerMajor = 0,
            ShowLabels = false,
        };

        // The red zone of a VU meter always starts at 0 VU; clearing the warning zone hides it.
        if (config.WarnFrom is not null)
        {
            arc.Zones.Add(new ScaleZone { From = zeroVu, To = 1, Style = ZoneStyle.Band, InnerRadius = ArcRadius - 0.5, OuterRadius = ArcRadius + 5.5 });
        }

        layers.Add(arc);

        foreach (var db in Minors)
        {
            layers.Add(ThemeKit.RadialLine(g, Fraction(db), ArcRadius - 4, ArcRadius, 1, "Minor tick"));
        }

        foreach (var (db, label) in Majors)
        {
            var f = Fraction(db);
            layers.Add(ThemeKit.RadialLine(g, f, ArcRadius - 8, ArcRadius, 2, $"Tick {label}"));
            layers.Add(ThemeKit.ArcText(g, f, ArcRadius - 16, label, "barlow-condensed", 12, 600, name: $"{label} VU"));
        }

        if (ThemeKit.Option(config, "percentScale", true))
        {
            layers.Add(ThemeKit.PivotArc(g, 0, zeroVu, PercentRadius, 1, "Percent arc"));
            for (var p = 0; p <= 100; p += 10)
            {
                var f = zeroVu * p / 100;
                var major = p % 20 == 0;
                layers.Add(ThemeKit.RadialLine(g, f, PercentRadius, PercentRadius + (major ? 5 : 3), 1, $"{p} %"));
                var (_, y) = g.PointAt(f, PercentRadius - 8);
                if (major && p > 0 && y < 136)
                {
                    layers.Add(ThemeKit.ArcText(g, f, PercentRadius - 8, p.ToString(System.Globalization.CultureInfo.InvariantCulture), "barlow-condensed", 10, 500, name: $"{p} %"));
                }
            }

            var (px, py) = g.PointAt(zeroVu + 0.07, PercentRadius - 8);
            layers.Add(new TextLayer { Name = "Percent sign", Text = "%", X = Math.Round(px), Y = Math.Round(py), Font = "barlow-condensed", Size = 10, Weight = 500, Baseline = TextBaseline.Middle, Smooth = false });
        }

        // Legend and title in the dome above the scale.
        var legend = ThemeKit.Option(config, "legend", "VU");
        layers.Add(new TextLayer
        {
            Name = "Legend",
            Text = legend.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal),
            X = ThemeKit.CenterX,
            Y = config.ShowSparkline ? 46 : 54,
            Baseline = TextBaseline.Baseline,
            Font = "libre-baskerville",
            Size = 30,
            Weight = 700,
            MaxWidth = 120,
            Overflow = TextOverflow.Shrink,
        });

        layers.Add(new TextLayer
        {
            Name = "Title",
            Text = ThemeKit.TitleTemplate(config),
            X = ThemeKit.CenterX,
            Y = config.ShowSparkline ? 9 : 13,
            Baseline = TextBaseline.Top,
            Font = "barlow-condensed",
            Size = 11,
            Weight = 600,
            Uppercase = true,
            LetterSpacing = 2,
            MaxWidth = 120,
            Overflow = TextOverflow.Ellipsis,
            Smooth = false,
        });

        if (ThemeKit.IconTemplate(config, context) is { } icon)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = 14, Y = 14, Size = 18 });
        }

        if (config.ShowValue)
        {
            layers.Add(new TextLayer
            {
                Name = "Value",
                Text = "{value}",
                X = 194,
                Y = 6,
                Align = TextAlign.Right,
                Baseline = TextBaseline.Top,
                Font = "barlow-condensed",
                Size = 12,
                Weight = 600,
                MaxWidth = 40,
                Overflow = TextOverflow.Shrink,
                Smooth = false,
            });
        }

        if (config.ShowSparkline)
        {
            layers.Add(ThemeKit.Sparkline(72, 55, 56, 12, stroke: 1));
        }

        if (ThemeKit.SubtitleTemplate(config, context) is { } subtitle)
        {
            layers.Add(ThemeKit.Subtitle(subtitle, ThemeKit.CenterX, 139, "barlow-condensed", 10, maxWidth: 80));
        }

        return doc;
    }
}
