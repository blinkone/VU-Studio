using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Themes.Minimal;

/// <summary>
/// minimal/arc — a thin continuous arc with a few ticks and small min/max labels, a large Inter value with a
/// small unit at the top centre, an uppercase letter-spaced title, and the icon inside the arc.
/// </summary>
internal sealed class ArcTheme : IThemeGenerator
{
    private const double ArcRadius = 146;

    public ThemeInfo Info { get; } = ThemeKit.Info(
        "minimal/arc",
        "Minimal arc",
        "Arco minimale",
        "A thin arc, a large live value and a spaced-out title. Clean and modern.",
        "Un arco sottile, un grande valore in tempo reale e un titolo spaziato. Pulito e moderno.",
        ThemeKit.BoolOption("ticks", "Ticks", "Tacche", true, "One tick per label count step.", "Una tacca per ogni passo del numero di etichette."),
        ThemeKit.BoolOption("rangeLabels", "Min/max labels", "Etichette min/max", true));

    public FaceDocument Generate(FaceConfig config, FaceRenderContext context)
    {
        var doc = new FaceDocument { Inverted = config.Inverted };
        var layers = doc.Layers;
        var sparkline = config.ShowSparkline;

        layers.Add(SpacedTitle(config, ThemeKit.CenterX, 7));

        if (config.ShowValue)
        {
            layers.AddRange(ThemeKit.ValueAndUnit(context, new ValueBlock
            {
                Baseline = sparkline ? 49 : 57,
                Font = "inter",
                Size = 36,
                Weight = 700,
                LetterSpacing = -0.5,
                UnitFont = "inter",
                UnitSize = 12,
                UnitWeight = 600,
                Placement = UnitPlacement.Top,
                Gap = 3,
                Left = 8,
                Right = 192,
            }));
        }

        if (sparkline)
        {
            layers.Add(ThemeKit.Sparkline(58, 56, 84, 14, stroke: 1));
        }

        var scale = new ScaleLayer
        {
            Name = "Scale",
            Radius = ArcRadius + 1,
            ShowArc = true,
            ArcRadius = ArcRadius,
            ArcWidth = 2,
            EvenSpacing = true,
            MajorCount = ThemeKit.Option(config, "ticks", true) ? ThemeKit.LabelCount(config) : 2,
            MinorPerMajor = 0,
            MajorLength = 7,
            MajorWidth = 2,
            ShowLabels = false,
        };
        if (ThemeKit.WarnZone(config, ZoneStyle.Band, ArcRadius - 2, ArcRadius + 3) is { } warn)
        {
            scale.Zones.Add(warn);
        }

        layers.Add(scale);

        if (ThemeKit.Option(config, "rangeLabels", true))
        {
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
                LabelWeight = 500,
            });
        }

        var subtitle = ThemeKit.SubtitleTemplate(config, context);
        if (ThemeKit.IconTemplate(config, context) is { } icon)
        {
            layers.Add(new IconLayer { Name = "Icon", Icon = icon, X = ThemeKit.CenterX, Y = subtitle is null ? 120 : 114, Size = 24 });
        }

        if (subtitle is not null)
        {
            layers.Add(new TextLayer
            {
                Name = "Subtitle",
                Text = subtitle,
                X = ThemeKit.CenterX,
                Y = 138,
                Font = "inter",
                Size = 9,
                Weight = 500,
                Baseline = TextBaseline.Baseline,
                MaxWidth = 110,
                Overflow = TextOverflow.Ellipsis,
                Smooth = false,
            });
        }

        return doc;
    }

    /// <summary>The uppercase, letter-spaced title used by the minimal themes.</summary>
    internal static TextLayer SpacedTitle(FaceConfig config, double x, double y, TextAlign align = TextAlign.Center, double maxWidth = 170) => new()
    {
        Name = "Title",
        Text = ThemeKit.TitleTemplate(config),
        X = x,
        Y = y,
        Align = align,
        Baseline = TextBaseline.Top,
        Font = "inter",
        Size = 9,
        Weight = 600,
        Uppercase = true,
        LetterSpacing = 1.8,
        MaxWidth = maxWidth,
        Overflow = TextOverflow.Ellipsis,
        Smooth = false,
    };
}
